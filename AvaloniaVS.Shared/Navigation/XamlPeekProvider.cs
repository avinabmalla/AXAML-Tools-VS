using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using AvaloniaVS.Models;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Serilog;

namespace AvaloniaVS.Navigation
{
    /// <summary>
    /// Peek Definition (Alt+F12) for Avalonia XAML, using the same targets as F12.
    /// </summary>
    [Export(typeof(IPeekableItemSourceProvider))]
    [ContentType("xml")]
    [Name("Avalonia XAML peek")]
    [SupportsStandaloneFiles(true)]
    internal sealed class XamlPeekableItemSourceProvider : IPeekableItemSourceProvider
    {
        [Import]
        internal IPeekResultFactory PeekResultFactory { get; set; }

        public IPeekableItemSource TryCreatePeekableItemSource(ITextBuffer textBuffer) =>
            textBuffer.Properties.ContainsProperty(typeof(XamlBufferMetadata))
                ? textBuffer.Properties.GetOrCreateSingletonProperty(() => new Source(textBuffer, PeekResultFactory))
                : null;

        private sealed class Source : IPeekableItemSource
        {
            private readonly ITextBuffer _buffer;
            private readonly IPeekResultFactory _factory;

            public Source(ITextBuffer buffer, IPeekResultFactory factory)
            {
                _buffer = buffer;
                _factory = factory;
            }

            public void AugmentPeekSession(IPeekSession session, IList<IPeekableItem> peekableItems)
            {
                if (!string.Equals(session.RelationshipName, PredefinedPeekRelationships.Definitions.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var point = session.GetTriggerPoint(_buffer.CurrentSnapshot);
                if (point is null)
                {
                    return;
                }

                var target = XamlNavigationService.Classify(session.TextView, point.Value);
                if (target != null)
                {
                    peekableItems.Add(new Item(session.TextView, target, _factory));
                }
            }

            public void Dispose()
            {
            }
        }

        private sealed class Item : IPeekableItem
        {
            private readonly ITextView _view;
            private readonly Avalonia.Ide.CompletionEngine.Navigation.XamlNavigationTarget _target;
            private readonly IPeekResultFactory _factory;

            public Item(ITextView view, Avalonia.Ide.CompletionEngine.Navigation.XamlNavigationTarget target, IPeekResultFactory factory)
            {
                _view = view;
                _target = target;
                _factory = factory;
            }

            public string DisplayName => _target.Text;

            public IEnumerable<IPeekRelationship> Relationships => new[] { PredefinedPeekRelationships.Definitions };

            public IPeekResultSource GetOrCreateResultSource(string relationshipName) => new ResultSource(this);

            private sealed class ResultSource : IPeekResultSource
            {
                private readonly Item _item;

                public ResultSource(Item item) => _item = item;

                public void FindResults(string relationshipName, IPeekResultCollection resultCollection, CancellationToken cancellationToken, IFindPeekResultsCallback callback)
                {
                    try
                    {
                        // Peek calls this on a background thread and expects results synchronously.
                        var locations = ThreadHelper.JoinableTaskFactory.Run(
                            () => XamlLocationService.GetDefinitionsAsync(_item._view, _item._target, cancellationToken));

                        foreach (var location in locations)
                        {
                            var label = System.IO.Path.GetFileName(location.Path);
                            var displayInfo = new PeekResultDisplayInfo(
                                label: $"{label} ({location.Line + 1})",
                                labelTooltip: location.Path,
                                title: label,
                                titleTooltip: location.Path);

                            resultCollection.Add(_item._factory.Create(
                                displayInfo,
                                location.Path,
                                location.Line, location.Column,
                                location.EndLine, location.EndColumn,
                                location.Line, location.Column));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Debug(ex, "Peek failed for {Target}", _item._target);
                        callback.ReportFailure(ex);
                    }
                }
            }
        }
    }
}
