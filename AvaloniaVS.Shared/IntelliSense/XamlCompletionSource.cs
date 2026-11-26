using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AvaloniaVS.Models;
using AvaloniaVS.Shared.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Serilog;

namespace AvaloniaVS.IntelliSense
{
    internal class XamlCompletionSource : ICompletionSource
    {
        private readonly ITextBuffer _buffer;
        private readonly CompletionEngineSource _engine;

        public XamlCompletionSource(ITextBuffer textBuffer, CompletionEngineSource completionEngineSource)
        {
            _buffer = textBuffer;
            _engine = completionEngineSource;
        }

        public void AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
        {
            if (_buffer.Properties.TryGetProperty<XamlBufferMetadata>(typeof(XamlBufferMetadata), out var metadata) &&
                metadata.CompletionMetadata != null)
            {
                var sw = Stopwatch.StartNew();
                var pos = session.TextView.Caret.Position.BufferPosition;
                var text = pos.Snapshot.GetText();
                _buffer.Properties.TryGetProperty("AssemblyName", out string assemblyName);
                var completions = _engine.CompletionEngine.GetCompletions(metadata.CompletionMetadata, text, pos, assemblyName);
                AddExistingEventHandlers(completions, pos.Snapshot, pos.Position);

                if (completions?.Completions.Count > 0)
                {
                    var start = completions.StartPosition;

                    // TODO: this should be handled in the completion engine
                    // pseudoclasses should only be returned in a Selector, so this is an easy filter
                    // We need to offset the start though for pseudoclasses to remove what they're 
                    // attached to: Control:pointerover -> :pointerover
                    if (completions.Completions[0].DisplayText.StartsWith(":"))
                    {
                        for (int i = pos - 1; i >= 0; i--)
                        {
                            if (char.IsWhiteSpace(text[i]) || text[i] == ':')
                            {
                                start = i;
                                break;
                            }
                        }
                    }

                    var span = new SnapshotSpan(pos.Snapshot, start, pos.Position - start);
                    var applicableTo = pos.Snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeInclusive);

                    completionSets.Insert(0, new CompletionSet(
                        "Avalonia",
                        "Avalonia",
                        applicableTo,
                        XamlCompletion.Create(completions.Completions),
                        null));

                    // This selects the first item in the completion popup - otherwise you have to physically
                    // interact with the completion list (either via mouse or keyboard arrows) otherwise tab
                    // or space won't trigger it
                    // TODO: We should really try to find the best match of the completion list and select that
                    // instead, but that's more than I want to do right now
                    if (completions.Completions.Count > 0)
                    {
                        completionSets[0].SelectionStatus = new CompletionSelectionStatus(
                            completionSets[0].Completions[0], true, false);
                    }

                    string completionHint = completions.Completions.Count == 0 ?
                        "no completions found" :
                        $"{completions.Completions.Count} completions found (First:{completions.Completions.FirstOrDefault()?.DisplayText})";

                    Log.Logger.Verbose("XAML completion took {Time}, {CompletionHint}", sw.Elapsed, completionHint);
                }

                sw.Stop();
            }
        }

        /// <summary>
        /// When completing an event attribute's value, also offer the compatible methods already in
        /// the code-behind, after "&lt;New Event Handler&gt;". Uses Roslyn's cached compilation only,
        /// so typing never waits for a build; the list fills in once Roslyn has one.
        /// </summary>
        private void AddExistingEventHandlers(Avalonia.Ide.CompletionEngine.CompletionSet completions, ITextSnapshot snapshot, int position)
        {
            var completionList = completions?.Completions;
            if (completionList is null ||
                !completionList.Any(c => c.DisplayText == Avalonia.Ide.CompletionEngine.CompletionEngine.NewEventHandlerDisplayText))
            {
                return;
            }

            try
            {
                var document = XamlDocumentCache.Get(snapshot);
                var eventContext = Navigation.EventAttributeContext.TryCreate(document, position, _engine.CompletionEngine.Helper);
                if (eventContext?.ClassName is null)
                {
                    return;
                }

                // Roslyn rarely keeps compilations cached, so wait briefly for one: listing the
                // existing handlers is worth a short pause when you open completion on an event.
                var context = Navigation.XamlRoslynContext.TryCreateQuickly(
                    Navigation.XamlRoslynContext.GetFilePath(_buffer), System.TimeSpan.FromMilliseconds(1500));
                var type = context?.GetType(eventContext.ClassName);
                var parameters = type is null
                    ? null
                    : Navigation.EventHandlerGenerator.ResolveParameters(context, eventContext.EventOwnerType, eventContext.EventName, eventContext.IsAttached);
                if (parameters is null)
                {
                    return;
                }

                var existing = Navigation.EventHandlerGenerator.FindCompatibleHandlers(context, type, parameters)
                    .Select(m => m.Name)
                    .Distinct()
                    .OrderBy(n => n, System.StringComparer.Ordinal)
                    .Where(n => !completionList.Any(c => c.InsertText == n && c.DisplayText != Avalonia.Ide.CompletionEngine.CompletionEngine.NewEventHandlerDisplayText))
                    .Select(n => new Avalonia.Ide.CompletionEngine.Completion(n, n, $"Existing handler in {type.Name}", Avalonia.Ide.CompletionEngine.CompletionKind.Event))
                    .ToList();

                completionList.AddRange(existing);
            }
            catch (System.Exception ex)
            {
                Log.Logger.Debug(ex, "Couldn't list existing event handlers");
            }
        }

        public void Dispose()
        {
        }
    }
}
