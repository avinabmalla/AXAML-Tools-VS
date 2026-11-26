using System;
using System.ComponentModel.Composition;
using AvaloniaVS.Models;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace AvaloniaVS.IntelliSense
{
    /// <summary>
    /// Registers a <see cref="XamlCompletionCommandHandler"/> with newly-created text views.
    /// </summary>
    [Name("Avalonia XAML manupulator")]
    [ContentType("xml")]
    [Export(typeof(IWpfTextViewCreationListener))]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    internal sealed class XamlTextViewCreationListener : IWpfTextViewCreationListener
    {
        private readonly IServiceProvider _serviceProvider;

        [ImportingConstructor]
        public XamlTextViewCreationListener(
            [Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void TextViewCreated(IWpfTextView textView)
        {            
            // If the buffer contains Avalonia XAML, register a completion handler on it.
            if (textView.TextBuffer.Properties.ContainsProperty(typeof(XamlBufferMetadata)))
            {
                new XamlTextManipulatorRegistrar(textView);

                // The designer already shows a preview beside the XAML, so the code map scrollbar
                // only takes space. Turn off VS's enhanced scrollbar entirely for these views so they
                // get the standard system scrollbar (bar mode alone still draws VS's own dark bar).
                // Other files keep the user's setting.
                textView.Options.SetOptionValue(DefaultTextViewHostOptions.ShowEnhancedScrollBarOptionId, false);
                textView.Options.SetOptionValue(DefaultTextViewHostOptions.SourceImageMarginEnabledOptionId, false);
                textView.Options.SetOptionValue(DefaultTextViewHostOptions.ShowPreviewOptionId, false);

                // Error List entries for this file shouldn't outlive its editor.
                textView.Closed += (_, _) =>
                {
                    if (textView.TextBuffer.Properties.TryGetProperty(typeof(BindingDataTypeTagger), out BindingDataTypeTagger tagger))
                    {
                        tagger.Clear();
                    }
                };
            }
        }
    }
}
