using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Avalonia.Ide.CompletionEngine.Navigation;
using AvaloniaVS.Models;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
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
        private readonly ITableManagerProvider _tableManagerProvider;

        [ImportingConstructor]
        public BindingDataTypeTaggerProvider(ITableManagerProvider tableManagerProvider)
        {
            _tableManagerProvider = tableManagerProvider;
        }

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (!buffer.Properties.TryGetProperty(typeof(XamlBufferMetadata), out XamlBufferMetadata metadata))
            {
                return null;
            }

            var tagger = buffer.Properties.GetOrCreateSingletonProperty(
                () => new BindingDataTypeTagger(buffer, metadata, _tableManagerProvider));
            return tagger as ITagger<T>;
        }
    }

    /// <summary>
    /// Warns about <c>{Binding}</c>s that won't compile because compiled bindings are on by default
    /// (Avalonia 12) and no <c>x:DataType</c> is in scope: a squiggle in the editor and an entry in
    /// the Error List.
    /// </summary>
    internal sealed class BindingDataTypeTagger : ITagger<IErrorTag>, ITableDataSource
    {
        public const string ErrorCode = "AVXAML001";

        public const string Message =
            "No x:DataType is in scope for this binding. Compiled bindings are on by default for this project " +
            "(the Avalonia 12 default), so it won't compile. Add x:DataType, or use {ReflectionBinding}. (Ctrl+. to add one)";

        private readonly ITextBuffer _buffer;
        private readonly XamlBufferMetadata _metadata;
        private readonly string _path;
        private ITextSnapshot _analyzedSnapshot;
        private IReadOnlyList<BindingWithoutDataType> _results = Array.Empty<BindingWithoutDataType>();
        private ITableDataSink _sink;
        private ITextSnapshot _publishedSnapshot;

        public BindingDataTypeTagger(ITextBuffer buffer, XamlBufferMetadata metadata, ITableManagerProvider tableManagerProvider)
        {
            _buffer = buffer;
            _metadata = metadata;
            _path = Navigation.XamlRoslynContext.GetFilePath(buffer);
            _buffer.Changed += (_, e) => RaiseTagsChanged(e.After);
            _metadata.CompiledBindingsByDefaultChanged += (_, _) =>
            {
                _publishedSnapshot = null;
                RaiseTagsChanged(_buffer.CurrentSnapshot);
            };

            tableManagerProvider.GetTableManager(StandardTables.ErrorsTable).AddSource(this,
                StandardTableColumnDefinitions.ErrorCode,
                StandardTableColumnDefinitions.ErrorSeverity,
                StandardTableColumnDefinitions.Text,
                StandardTableColumnDefinitions.DocumentName,
                StandardTableColumnDefinitions.Line,
                StandardTableColumnDefinitions.Column);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        string ITableDataSource.SourceTypeIdentifier => StandardTableDataSources.ErrorTableDataSource;

        string ITableDataSource.Identifier => "Avalonia XAML compiled binding checks";

        string ITableDataSource.DisplayName => "AXAML Tools";

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
            Analyze(snapshot);
            PublishToErrorList(snapshot);
            return _results;
        }

        /// <summary>Removes this file's entries from the Error List (when its editor closes).</summary>
        public void Clear()
        {
            _publishedSnapshot = null;
            _sink?.RemoveAllEntries();
        }

        IDisposable ITableDataSource.Subscribe(ITableDataSink sink)
        {
            _sink = sink;
            _publishedSnapshot = null;
            PublishToErrorList(_buffer.CurrentSnapshot);
            return null;
        }

        private void Analyze(ITextSnapshot snapshot)
        {
            if (_analyzedSnapshot != snapshot)
            {
                _results = BindingDataTypeAnalyzer.Analyze(XamlDocumentCache.Get(snapshot));
                _analyzedSnapshot = snapshot;
            }
        }

        private void PublishToErrorList(ITextSnapshot snapshot)
        {
            if (_sink is null || _publishedSnapshot == snapshot)
            {
                return;
            }

            _publishedSnapshot = snapshot;

            if (_metadata.CompiledBindingsByDefault != true)
            {
                _sink.RemoveAllEntries();
                return;
            }

            Analyze(snapshot);
            var entries = _results
                .Where(r => r.Start < snapshot.Length)
                .Select(r =>
                {
                    var line = snapshot.GetLineFromPosition(r.Start);
                    return (ITableEntry)new Entry(_path, line.LineNumber, r.Start - line.Start.Position);
                })
                .ToList();

            _sink.AddEntries(entries, removeAllEntries: true);
        }

        private void RaiseTagsChanged(ITextSnapshot snapshot) =>
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));

        private sealed class Entry : TableEntryBase
        {
            private readonly string _path;
            private readonly int _line;
            private readonly int _column;

            public Entry(string path, int line, int column)
            {
                _path = path;
                _line = line;
                _column = column;
            }

            public override bool TryGetValue(string keyName, out object content)
            {
                switch (keyName)
                {
                    case StandardTableKeyNames.ErrorCode:
                        content = ErrorCode;
                        return true;
                    case StandardTableKeyNames.ErrorSeverity:
                        content = __VSERRORCATEGORY.EC_WARNING;
                        return true;
                    case StandardTableKeyNames.Text:
                        content = Message;
                        return true;
                    case StandardTableKeyNames.DocumentName:
                        content = _path;
                        return _path != null;
                    case StandardTableKeyNames.Line:
                        content = _line;
                        return true;
                    case StandardTableKeyNames.Column:
                        content = _column;
                        return true;
                    case StandardTableKeyNames.BuildTool:
                        content = "AXAML Tools";
                        return true;
                    default:
                        content = null;
                        return false;
                }
            }
        }
    }
}
