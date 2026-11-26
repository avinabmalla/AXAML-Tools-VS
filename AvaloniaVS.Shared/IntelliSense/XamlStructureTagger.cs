using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Avalonia.Ide.CompletionEngine.Navigation;
using AvaloniaVS.Models;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace AvaloniaVS.IntelliSense
{
    [Export(typeof(ITaggerProvider))]
    [ContentType("xml")]
    [TagType(typeof(IStructureTag))]
    internal sealed class XamlStructureTaggerProvider : ITaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (!buffer.Properties.ContainsProperty(typeof(XamlBufferMetadata)))
            {
                return null;
            }

            var tagger = buffer.Properties.GetOrCreateSingletonProperty(() => new XamlStructureTagger(buffer));
            return tagger as ITagger<T>;
        }
    }

    /// <summary>
    /// Indent guides for Avalonia XAML: a vertical line from each multi-line element's <c>&lt;</c>
    /// down to its end tag. The XML editor already provides outlining, so these blocks aren't
    /// collapsible (otherwise every element would get two collapse buttons).
    /// </summary>
    internal sealed class XamlStructureTagger : ITagger<IStructureTag>
    {
        private readonly ITextBuffer _buffer;
        private ITextSnapshot _snapshot;
        private IReadOnlyList<XamlBlock> _blocks = Array.Empty<XamlBlock>();

        public XamlStructureTagger(ITextBuffer buffer)
        {
            _buffer = buffer;
            _buffer.Changed += (_, e) =>
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(e.After, 0, e.After.Length)));
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<IStructureTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0)
            {
                yield break;
            }

            var snapshot = spans[0].Snapshot;
            if (_snapshot != snapshot)
            {
                _blocks = XamlLayout.GetBlocks(snapshot.GetText());
                _snapshot = snapshot;
            }

            foreach (var block in _blocks)
            {
                if (block.EndTagStart > snapshot.Length)
                {
                    continue;
                }

                var span = new Span(block.Start, block.EndTagStart - block.Start);
                if (!spans.IntersectsWith(new NormalizedSnapshotSpanCollection(new SnapshotSpan(snapshot, span))))
                {
                    continue;
                }

                // Header = the element's first line; the guide runs from the '<' to the end tag.
                var headerEnd = snapshot.GetLineFromPosition(block.Start).End.Position;
                var tag = new StructureTag(
                    snapshot,
                    outliningSpan: null,
                    headerSpan: new Span(block.Start, headerEnd - block.Start),
                    guideLineSpan: span,
                    guideLineHorizontalAnchor: block.Start,
                    type: PredefinedStructureTagTypes.Structural,
                    isCollapsible: false,
                    isDefaultCollapsed: false,
                    isImplementation: false,
                    collapsedForm: null,
                    collapsedHintForm: null);

                yield return new TagSpan<IStructureTag>(new SnapshotSpan(snapshot, span), tag);
            }
        }
    }
}
