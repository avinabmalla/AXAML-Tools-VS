using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace AvaloniaVS.Navigation
{
    internal static class CtrlClickLinkClassification
    {
        public const string Name = "AXAML Ctrl+Click link";

        [Export]
        [Name(Name)]
        internal static ClassificationTypeDefinition TypeDefinition = null;
    }

    /// <summary>Underlines the target under the mouse while Ctrl is held, like C#'s Ctrl+Click.</summary>
    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = CtrlClickLinkClassification.Name)]
    [Name(CtrlClickLinkClassification.Name)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class CtrlClickLinkFormat : ClassificationFormatDefinition
    {
        public CtrlClickLinkFormat()
        {
            DisplayName = "AXAML Ctrl+Click link";
            TextDecorations = System.Windows.TextDecorations.Underline;
        }
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType("xml")]
    [TagType(typeof(ClassificationTag))]
    internal sealed class CtrlClickLinkTaggerProvider : IViewTaggerProvider
    {
        [Import]
        internal IClassificationTypeRegistryService ClassificationRegistry { get; set; }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (textView.TextBuffer != buffer)
            {
                return null;
            }

            var type = ClassificationRegistry.GetClassificationType(CtrlClickLinkClassification.Name);
            return textView.Properties.GetOrCreateSingletonProperty(() => new CtrlClickLinkTagger(type)) as ITagger<T>;
        }
    }

    /// <summary>Holds the span currently shown as a link; set by the Ctrl+Click mouse processor.</summary>
    internal sealed class CtrlClickLinkTagger : ITagger<ClassificationTag>
    {
        private readonly ClassificationTag _tag;
        private SnapshotSpan? _span;

        public CtrlClickLinkTagger(IClassificationType type) => _tag = new ClassificationTag(type);

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public SnapshotSpan? Span => _span;

        public void SetSpan(SnapshotSpan? span)
        {
            if (_span == span)
            {
                return;
            }

            var old = _span;
            _span = span;

            if (old is { } previous)
            {
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(previous));
            }

            if (span is { } current)
            {
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(current));
            }
        }

        public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (_span is not { } span || spans.Count == 0)
            {
                yield break;
            }

            var translated = span.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive);
            if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(translated)))
            {
                yield return new TagSpan<ClassificationTag>(translated, _tag);
            }
        }
    }
}
