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
    [TagType(typeof(IErrorTag))]
    internal sealed class BindingDataTypeTaggerProvider : ITaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (!buffer.Properties.TryGetProperty(typeof(XamlBufferMetadata), out XamlBufferMetadata metadata))
            {
                return null;
            }

            var tagger = buffer.Properties.GetOrCreateSingletonProperty(() => new BindingDataTypeTagger(buffer, metadata));
            return tagger as ITagger<T>;
        }
    }

    /// <summary>
    /// Warns about <c>{Binding}</c>s that won't compile because compiled bindings are on by default
    /// (Avalonia 12) and no <c>x:DataType</c> is in scope.
    /// </summary>
    internal sealed class BindingDataTypeTagger : ITagger<IErrorTag>
    {
        public const string Message =
            "No x:DataType is in scope for this binding. Compiled bindings are on by default for this project " +
            "(the Avalonia 12 default), so it won't compile. Add x:DataType, or use {ReflectionBinding}. (Ctrl+. to add one)";

        private readonly ITextBuffer _buffer;
        private readonly XamlBufferMetadata _metadata;
        private ITextSnapshot _analyzedSnapshot;
        private IReadOnlyList<BindingWithoutDataType> _results = Array.Empty<BindingWithoutDataType>();

        public BindingDataTypeTagger(ITextBuffer buffer, XamlBufferMetadata metadata)
        {
            _buffer = buffer;
            _metadata = metadata;
            _buffer.Changed += (_, e) => RaiseTagsChanged(e.After);
            _metadata.CompiledBindingsByDefaultChanged += (_, _) => RaiseTagsChanged(_buffer.CurrentSnapshot);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || _metadata.CompiledBindingsByDefault != true)
            {
                yield break;
            }

            var snapshot = spans[0].Snapshot;
            foreach (var result in GetResults(snapshot))
            {
                if (result.Start + result.Length > snapshot.Length)
                {
                    continue;
                }

                var span = new SnapshotSpan(snapshot, result.Start, result.Length);
                if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(span)))
                {
                    yield return new TagSpan<IErrorTag>(span, new ErrorTag(PredefinedErrorTypeNames.Warning, Message));
                }
            }
        }

        /// <summary>The bindings without a data type in <paramref name="snapshot"/>, cached per version.</summary>
        public IReadOnlyList<BindingWithoutDataType> GetResults(ITextSnapshot snapshot)
        {
            if (_analyzedSnapshot != snapshot)
            {
                _results = BindingDataTypeAnalyzer.Analyze(snapshot.GetText());
                _analyzedSnapshot = snapshot;
            }

            return _results;
        }

        private void RaiseTagsChanged(ITextSnapshot snapshot) =>
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }
}
