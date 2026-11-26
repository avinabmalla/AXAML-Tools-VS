using System;
using Avalonia.Ide.CompletionEngine.Navigation;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Serilog;

namespace AvaloniaVS.IntelliSense
{
    /// <summary>
    /// XAML-aware indentation on Enter. The XML editor indents a new line inside a start tag to
    /// the previous line, and content after a multi-line start tag to the last attribute's column.
    /// This lets the editor insert the newline, then fixes the new line's indentation:
    /// attributes align with the first attribute, content goes one level in from the element,
    /// and end tags line up with their element. See <see cref="XamlLayout.GetIndentColumn"/>.
    /// </summary>
    internal sealed class XamlIndentCommandHandler : IOleCommandTarget
    {
        private readonly ITextView _textView;
        private readonly ITextUndoHistoryRegistry _undoHistoryRegistry;
        private readonly Microsoft.VisualStudio.Language.Intellisense.ICompletionBroker _completionBroker;
        private readonly IOleCommandTarget _next;

        public XamlIndentCommandHandler(
            ITextView textView,
            IVsTextView textViewAdapter,
            ITextUndoHistoryRegistry undoHistoryRegistry,
            Microsoft.VisualStudio.Language.Intellisense.ICompletionBroker completionBroker)
        {
            _textView = textView;
            _undoHistoryRegistry = undoHistoryRegistry;
            _completionBroker = completionBroker;
            textViewAdapter.AddCommandFilter(this, out _next);
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
            _next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (pguidCmdGroup != VSConstants.VSStd2K || nCmdID != (uint)VSConstants.VSStd2KCmdID.RETURN)
            {
                return _next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            }

            var lineBefore = _textView.Caret.Position.BufferPosition.GetContainingLine().LineNumber;

            // One undo step for the newline and the re-indent: the editor's own Enter transaction
            // nests inside this one. Always Complete it - disposing an uncompleted transaction
            // would roll the newline back.
            _undoHistoryRegistry.TryGetHistory(_textView.TextBuffer, out var history);
            using var transaction = history?.CreateTransaction("Insert new line");

            // Enter right before an attribute's closing quote means "done with this value": step
            // past the quote so the newline goes after it, not inside the value. Not while a
            // completion list is open (Enter commits it), and Shift+Enter always inserts a plain
            // newline.
            if (!_completionBroker.IsCompletionActive(_textView) &&
                (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
            {
                try
                {
                    var exit = XamlLayout.GetAttributeExitPosition(
                        XamlDocumentCache.Get(_textView.TextSnapshot), _textView.Caret.Position.BufferPosition.Position);
                    if (exit is int position)
                    {
                        _textView.Caret.MoveTo(new SnapshotPoint(_textView.TextSnapshot, position));
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Debug(ex, "XAML attribute exit check failed");
                }
            }

            var result = _next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);

            // Only when Enter actually started a new line (not when it committed a completion).
            if (ErrorHandler.Succeeded(result) &&
                _textView.Caret.Position.BufferPosition.GetContainingLine().LineNumber > lineBefore)
            {
                try
                {
                    FixIndentation();
                }
                catch (Exception ex)
                {
                    Log.Logger.Debug(ex, "XAML smart indent failed");
                }
            }

            transaction?.Complete();
            return result;
        }

        private void FixIndentation()
        {
            var snapshot = _textView.TextSnapshot;
            var caret = _textView.Caret.Position;
            var line = caret.BufferPosition.GetContainingLine();

            var tabSize = _textView.Options.GetOptionValue(DefaultOptions.TabSizeOptionId);
            var indentSize = _textView.Options.GetOptionValue(DefaultOptions.IndentSizeOptionId);
            var useTabs = !_textView.Options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId);

            var column = XamlLayout.GetIndentColumn(XamlDocumentCache.Get(snapshot), line.Start.Position, tabSize, indentSize);
            if (column is null)
            {
                return;
            }

            var desired = XamlLayout.MakeIndent(column.Value, useTabs, tabSize);

            var whitespaceEnd = line.Start.Position;
            while (whitespaceEnd < line.End.Position && char.IsWhiteSpace(snapshot[whitespaceEnd]))
            {
                whitespaceEnd++;
            }

            var current = snapshot.GetText(line.Start.Position, whitespaceEnd - line.Start.Position);
            var caretInIndent = caret.BufferPosition.Position <= whitespaceEnd;

            if (current != desired)
            {
                _textView.TextBuffer.Replace(new Span(line.Start.Position, current.Length), desired);
            }

            if (caretInIndent)
            {
                // Put the caret after the indentation (this also clears any virtual space the
                // editor used for an empty line).
                var newLine = _textView.TextSnapshot.GetLineFromLineNumber(line.LineNumber);
                _textView.Caret.MoveTo(new SnapshotPoint(_textView.TextSnapshot, newLine.Start.Position + desired.Length));
            }
        }
    }
}
