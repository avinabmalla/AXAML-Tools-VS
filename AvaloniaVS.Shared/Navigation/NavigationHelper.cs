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
