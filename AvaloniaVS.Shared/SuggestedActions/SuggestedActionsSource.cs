using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Avalonia.Ide.CompletionEngine;
using AvaloniaVS.Models;
using AvaloniaVS.Shared.SuggestedActions.Actions;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;

namespace AvaloniaVS.Shared.SuggestedActions
{
    class SuggestedActionsSource : ISuggestedActionsSource
    {
        private readonly SuggestedActionsSourceProvider _factory;
        private readonly ITextBuffer _textBuffer;
        private readonly IWpfDifferenceViewerFactoryService _diffFactory;
        private readonly IDifferenceBufferFactoryService _diffBufferFactory;
        private readonly ITextBufferFactoryService _bufferFactory;
        private readonly ITextEditorFactoryService _textEditorFactoryService;
        private readonly ITextView _textView;

        public SuggestedActionsSource(SuggestedActionsSourceProvider testSuggestedActionsSourceProvider, ITextView textView, ITextBuffer textBuffer,
            IWpfDifferenceViewerFactoryService diffFactory, IDifferenceBufferFactoryService diffBufferFactory, ITextBufferFactoryService bufferFactory,
            ITextEditorFactoryService textEditorFactoryService)
        {
            _factory = testSuggestedActionsSourceProvider;
            _textBuffer = textBuffer;
            _diffFactory = diffFactory;
            _diffBufferFactory = diffBufferFactory;
            _bufferFactory = bufferFactory;
            _textEditorFactoryService = textEditorFactoryService;
            _textView = textView;
        }

        public event EventHandler<EventArgs> SuggestedActionsChanged;

        public void Dispose()
        {
        }

        public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range,
            CancellationToken cancellationToken)
        {
            var dataTypeActions = GetDataTypeActions(range);
            var availableSuggestedActions = SuggestedActionsAreAvailable(range);
            if (TryGetWordUnderCaret(out var extent) && (availableSuggestedActions.Item1 || availableSuggestedActions.Item2 || availableSuggestedActions.Item3))
            {
                extent.Span.Snapshot.TextBuffer.Properties.TryGetProperty<XamlBufferMetadata>(typeof(XamlBufferMetadata), out var metadata);
                var trackingSpan = range.Snapshot.CreateTrackingSpan(extent.Span, SpanTrackingMode.EdgeInclusive);
                ISuggestedAction suggestedAction = null;
                if (availableSuggestedActions.Item1)
                {
                    suggestedAction = new MissingNamespaceAndAliasSuggestedAction(trackingSpan, _diffFactory, _diffBufferFactory, _bufferFactory, _textEditorFactoryService,
    metadata.CompletionMetadata.InverseNamespace, CompletionEngine.GetNamespaceAliases(extent.Span.Snapshot.TextBuffer.CurrentSnapshot.GetText()));
                }
                else if (availableSuggestedActions.Item2)
                {
                    suggestedAction = new MissingAliasSuggestedAction(trackingSpan, _diffFactory, _diffBufferFactory, _bufferFactory, _textEditorFactoryService,
    metadata.CompletionMetadata.InverseNamespace);
                }
                else if (availableSuggestedActions.Item3)
                {
                    HasAlias(out var alias);
                    suggestedAction = new MissingNamespaceSuggestedAction(trackingSpan, _diffFactory, _diffBufferFactory, _bufferFactory, _textEditorFactoryService,
    metadata.CompletionMetadata.InverseNamespace, CompletionEngine.GetNamespaceAliases(extent.Span.Snapshot.TextBuffer.CurrentSnapshot.GetText()), alias);
                }
                var sets = new List<SuggestedActionSet> { new SuggestedActionSet(new ISuggestedAction[] { suggestedAction }) };
                if (dataTypeActions.Count > 0)
                {
                    sets.Add(new SuggestedActionSet(dataTypeActions));
                }

                return sets;
            }

            return dataTypeActions.Count > 0
                ? new[] { new SuggestedActionSet(dataTypeActions) }
                : Enumerable.Empty<SuggestedActionSet>();
        }

        /// <summary>
        /// "Add x:DataType" actions when the caret is on a binding with no x:DataType in scope.
        /// </summary>
        private IReadOnlyList<ISuggestedAction> GetDataTypeActions(SnapshotSpan range)
        {
            if (!_textBuffer.Properties.TryGetProperty<XamlBufferMetadata>(typeof(XamlBufferMetadata), out var metadata) ||
                metadata.CompletionMetadata?.InverseNamespace is not { } inverseNamespace)
            {
                return Array.Empty<ISuggestedAction>();
            }

            var snapshot = _textBuffer.CurrentSnapshot;
            var position = range.Snapshot == snapshot ? range.Start.Position : _textView.Caret.Position.BufferPosition.Position;
            var bindings = _textBuffer.Properties.TryGetProperty<AvaloniaVS.IntelliSense.BindingDataTypeTagger>(typeof(AvaloniaVS.IntelliSense.BindingDataTypeTagger), out var tagger)
                ? tagger.GetResults(snapshot)
                : Avalonia.Ide.CompletionEngine.Navigation.BindingDataTypeAnalyzer.Analyze(snapshot.GetText());

            var binding = bindings.FirstOrDefault(b => position >= b.Start && position <= b.Start + b.Length);
            return binding is null
                ? Array.Empty<ISuggestedAction>()
                : AddDataTypeSuggestedAction.Create(_textBuffer, binding, inverseNamespace).ToList();
        }

        public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
        {
            var availableSuggestedActions = SuggestedActionsAreAvailable(range);
            if (availableSuggestedActions.Item1 || availableSuggestedActions.Item2 || availableSuggestedActions.Item3)
            {
                return Task.FromResult(true);
            }

            if (GetDataTypeActions(range).Count > 0)
            {
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        private bool TryGetWordUnderCaret(out TextExtent wordExtent)
        {
            var caret = _textView.Caret;
            SnapshotPoint point;

            if (caret.Position.BufferPosition > 0)
            {
                point = caret.Position.BufferPosition - 1;
            }
            else
            {
                wordExtent = default;
                return false;
            }

            var navigator = _factory.NavigatorService.GetTextStructureNavigator(_textBuffer);

            wordExtent = navigator.GetExtentOfWord(point);
            return true;
        }


        /// <returns>
        /// This method returns 3 bool values. First one defines whether MissingNamespaceAndAliasSuggestedAction should be applied
        /// Second one defines whether MissingAliasSuggestedAction should be applied.
        /// Third one defines whether MissingNamespaceSuggestedAction should be applied.
        /// </returns>
        private (bool, bool, bool) SuggestedActionsAreAvailable(SnapshotSpan range)
        {
            if (TryGetWordUnderCaret(out var extent))
            {
                var span = range.Snapshot.CreateTrackingSpan(extent.Span, SpanTrackingMode.EdgeInclusive);
                var snapshot = span.TextBuffer.CurrentSnapshot;
                var targetClassName = span.GetText(snapshot);
                span.TextBuffer.Properties.TryGetProperty<XamlBufferMetadata>(typeof(XamlBufferMetadata), out var metadata);
                if (metadata == null || metadata.CompletionMetadata?.InverseNamespace == null)
                {
                    return (false, false, false);
                }
                var targetClassMetadata = metadata.CompletionMetadata.InverseNamespace.FirstOrDefault(x => x.Key.Split('.').Last() == targetClassName);

                // Exclude all classes from avaloniaui namespace because controls from this namespace are included by default.
                if (targetClassMetadata.Value != null && targetClassMetadata.Key != null && !metadata.CompletionMetadata.Namespaces.First(x => x.Key == "https://github.com/avaloniaui").Value.ContainsKey(targetClassName))
                {
                    if (!CompletionEngine.GetNamespaceAliases(span.TextBuffer.CurrentSnapshot.GetText()).ContainsValue(targetClassMetadata.Value))
                    {
                        if (!HasAlias(out var _))
                        {
                            return (true, false, false);
                        }
                        else
                        {
                            return (false, false, true);
                        }
                    }
                    else if (!HasAlias(out var _))
                    {
                        return (false, true, false);
                    }
                }

            }
            return (false, false, false);
        }

        private bool HasAlias(out string alias)
        {

            var span = _textView.Caret.ContainingTextViewLine.Extent.GetText().Trim();
            var xmlReader = XmlReader.Create(new StringReader(span));
            try
            {
                xmlReader.Read();
            }
            catch
            {
                if (xmlReader.NodeType == XmlNodeType.Element && !string.IsNullOrEmpty(xmlReader.Prefix))
                {
                    alias = xmlReader.Prefix;
                    return true;
                }
            }
            alias = null;
            return false;
        }
    }
}
