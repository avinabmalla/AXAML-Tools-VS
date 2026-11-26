using System;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace AvaloniaVS.Services
{
    /// <summary>
    /// A serilog sink that outputs to the VS output window.
    /// </summary>
    internal class OutputPaneEventSink : ILogEventSink
    {
        private static readonly Guid paneGuid = new Guid("DFDA891A-C5F5-441F-9021-521A2AE9D0D2");
        private readonly IVsOutputWindowPane _pane;
        private readonly ITextFormatter _formatter;

        /// <summary>
        /// Initializes a new instance of the <see cref="OutputPaneEventSink"/> class.
        /// </summary>
        /// <param name="output">The VS output window.</param>
        /// <param name="outputTemplate">The serilog output template.</param>
        public OutputPaneEventSink(
            IVsOutputWindow output,
            string outputTemplate)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _formatter = new MessageTemplateTextFormatter(outputTemplate, null);
            ErrorHandler.ThrowOnFailure(output.CreatePane(paneGuid, "Avalonia XAML Tools", 1, 1));
            output.GetPane(paneGuid, out _pane);
        }

#pragma warning disable VSTHRD010
        /// <inheritdoc/>
        public void Emit(LogEvent logEvent)
        {
            var sw = new StringWriter();
            _formatter.Format(logEvent, sw);
            var message = sw.ToString();

            if (_pane is IVsOutputWindowPaneNoPump noPump)
            {
                noPump.OutputStringNoPump(message);
            }
            else
            {
                ErrorHandler.ThrowOnFailure(_pane.OutputStringThreadSafe(message));
            }

            if (logEvent.Level == LogEventLevel.Error)
            {
                _pane.Activate();
            }
        }
#pragma warning restore VSTHRD010
    }
}
