using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Serilog;
using Task = System.Threading.Tasks.Task;

namespace AvaloniaVS.Navigation
{
    internal static class NavigationHelper
    {
        /// <summary>
        /// Navigates to <paramref name="symbol"/>: its hand-written source if it has one, otherwise
        /// via Roslyn (which shows metadata-as-source or decompiled source for library types).
        /// </summary>
        public static async Task<bool> GoToSymbolAsync(XamlRoslynContext context, ISymbol symbol, CancellationToken cancellationToken)
        {
            var location = XamlRoslynContext.GetBestLocation(symbol);
            if (location?.SourceTree?.FilePath is { } path && !XamlRoslynContext.IsGenerated(path))
            {
                return await OpenAtAsync(path, location.SourceSpan.Start);
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return await context.Workspace.TryGoToDefinitionAsync(symbol, context.Project, cancellationToken);
        }

        /// <summary>Opens <paramref name="location"/> (in source) in an editor.</summary>
        public static Task<bool> OpenAtAsync(Location location) =>
            location?.SourceTree?.FilePath is { } path
                ? OpenAtAsync(path, location.SourceSpan.Start)
                : Task.FromResult(false);

        /// <summary>
        /// Opens <paramref name="path"/> and puts the caret at character <paramref name="offset"/>.
        /// </summary>
        public static async Task<bool> OpenAtAsync(string path, int offset)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                // Record where we're leaving from first. Recording only the destination (after the
                // target document is already active) leaves the starting point out of the history,
                // so Back skipped past it.
                RecordCurrentPosition(path);

                VsShellUtilities.OpenDocument(
                    ServiceProvider.GlobalProvider,
                    path,
                    Guid.Empty,
                    out _,
                    out _,
                    out var frame);

                if (frame is null)
                {
                    return false;
                }

                frame.Show();

                // Go through the text manager so the jump is recorded in VS's navigation history:
                // that's what Navigate Backward (Ctrl+-, the mouse Back button) walks. Placing the
                // caret directly, as below, doesn't record anything.
                RecordNavigation(frame, offset);

                var textView = VsShellUtilities.GetTextView(frame);
                if (textView is null)
                {
                    // Our Avalonia editor hosts its own text view: find it through the frame's view.
                    if (ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView)) &&
                        docView is AvaloniaVS.Shared.Views.EditorPane pane && pane.TextView is { } wpfView)
                    {
                        MoveCaret(wpfView, offset);
                        return true;
                    }

                    return false;
                }

                ErrorHandler.ThrowOnFailure(textView.GetLineAndColumn(offset, out var line, out var column));
                textView.SetCaretPos(line, column);
                textView.CenterLines(line, 1);
                return true;
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Failed to open {Path} at {Offset}", path, offset);
                return false;
            }
        }

        /// <summary>
        /// Navigates the frame's text buffer to <paramref name="offset"/> with
        /// <see cref="IVsTextManager.NavigateToLineAndColumn"/>, which adds the previous and new
        /// locations to the navigation history. Best effort: failures are only logged.
        /// </summary>
        private static void RecordNavigation(IVsWindowFrame frame, int offset)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out var docData)) ||
                    docData is not IVsTextLines lines ||
                    ServiceProvider.GlobalProvider.GetService(typeof(SVsTextManager)) is not IVsTextManager textManager)
                {
                    return;
                }

                if (!ErrorHandler.Succeeded(lines.GetLineIndexOfPosition(offset, out var line, out var column)))
                {
                    return;
                }

                var logicalView = VSConstants.LOGVIEWID.TextView_guid;
                textManager.NavigateToLineAndColumn(lines, ref logicalView, line, column, line, column);
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Couldn't record navigation history");
            }
        }

        /// <summary>
        /// Adds the active editor's caret position to VS's navigation history, and to our own
        /// <see cref="NavigationTrail"/> (which is what makes Back work in the XAML designer pane).
        /// </summary>
        private static void RecordCurrentPosition(string destinationPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (ServiceProvider.GlobalProvider.GetService(typeof(SVsTextManager)) is not IVsTextManager textManager ||
                    !ErrorHandler.Succeeded(textManager.GetActiveView(1, null, out var activeView)) ||
                    activeView is null ||
                    !ErrorHandler.Succeeded(activeView.GetBuffer(out var lines)) ||
                    !ErrorHandler.Succeeded(activeView.GetCaretPos(out var line, out var column)))
                {
                    return;
                }

                if (ErrorHandler.Succeeded(lines.GetPositionOfLineIndex(line, column, out var offset)) &&
                    lines is IVsUserData userData)
                {
                    // The buffer's moniker is its file path.
                    var monikerGuid = VSConstants.VsTextBufferUserDataGuid.VsBufferMoniker_guid;
                    if (ErrorHandler.Succeeded(userData.GetData(ref monikerGuid, out var moniker)) && moniker is string fromPath)
                    {
                        NavigationTrail.Record(fromPath, offset, destinationPath);
                    }
                }

                var logicalView = VSConstants.LOGVIEWID.TextView_guid;
                textManager.NavigateToLineAndColumn(lines, ref logicalView, line, column, line, column);
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "Couldn't record the starting position in navigation history");
            }
        }

        /// <summary>Moves the caret in an already-open view and brings it into view.</summary>
        public static void MoveCaret(ITextView view, int offset)
        {
            var snapshot = view.TextSnapshot;
            var point = new SnapshotPoint(snapshot, Math.Max(0, Math.Min(offset, snapshot.Length)));
            view.Caret.MoveTo(point);
            view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, 0), EnsureSpanVisibleOptions.AlwaysCenter);
            System.Windows.Input.Keyboard.Focus(view is IWpfTextView wpf ? wpf.VisualElement : null);
        }

        public static async Task SetStatusAsync(string message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar statusbar)
            {
                statusbar.SetText(message);
            }
        }
    }
}
