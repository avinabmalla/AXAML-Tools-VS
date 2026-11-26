using System;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Serilog;

namespace AvaloniaVS.Navigation
{
    /// <summary>
    /// Handles Go To Definition (F12) and Go To Implementation (Ctrl+F12) in Avalonia XAML.
    /// Anything it can't resolve falls through to the next command handler.
    /// </summary>
    internal sealed class XamlNavigationCommandHandler : IOleCommandTarget
    {
        private readonly ITextView _textView;
        private readonly IOleCommandTarget _next;
        private readonly Guid _goToImplementationGroup;
        private readonly uint _goToImplementationId;

        public XamlNavigationCommandHandler(ITextView textView, IVsTextView textViewAdapter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _textView = textView;

            // Go To Implementation is a Roslyn command with no VSConstants entry, so look up its
            // GUID/ID by name rather than hard-coding them.
            try
            {
                if (Package.GetGlobalService(typeof(DTE)) is DTE dte &&
                    dte.Commands.Item("Edit.GoToImplementation") is Command command)
                {
                    _goToImplementationGroup = new Guid(command.Guid);
                    _goToImplementationId = (uint)command.ID;
                }
            }
            catch (Exception ex)
            {
                // Command not registered (no C# tooling) or DTE not ready: F12 still works.
                // Never let this escape: it runs while the editor view is being created, and an
                // exception here makes opening the file fail with "Catastrophic failure".
                Log.Logger.Debug(ex, "Edit.GoToImplementation not available; Ctrl+F12 won't be handled");
            }

            textViewAdapter.AddCommandFilter(this, out _next);
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            var result = _next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);

            // The XML editor doesn't enable these in .axaml files; we do.
            for (var i = 0; i < cCmds; i++)
            {
                if (IsNavigationCommand(pguidCmdGroup, prgCmds[i].cmdID, out _))
                {
                    prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                    result = VSConstants.S_OK;
                }
            }

            return result;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (IsNavigationCommand(pguidCmdGroup, nCmdID, out var implementation))
            {
                var target = XamlNavigationService.Classify(_textView, _textView.Caret.Position.BufferPosition);
                if (target != null)
                {
                    Log.Logger.Debug("Navigating to {Target} (implementation: {Implementation})", target, implementation);
                    ThreadHelper.JoinableTaskFactory
                        .RunAsync(() => XamlNavigationService.NavigateAsync(_textView, target, implementation, CancellationToken.None))
                        .FileAndForget("AvaloniaVS/XamlNavigation");
                    return VSConstants.S_OK;
                }
            }

            return _next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        private bool IsNavigationCommand(Guid group, uint id, out bool implementation)
        {
            implementation = false;

            if (group == VSConstants.GUID_VSStandardCommandSet97 && id == (uint)VSConstants.VSStd97CmdID.GotoDefn)
            {
                return true;
            }

            if (_goToImplementationId != 0 && group == _goToImplementationGroup && id == _goToImplementationId)
            {
                implementation = true;
                return true;
            }

            return false;
        }
    }
}
