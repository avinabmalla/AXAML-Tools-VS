using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Serilog;

namespace AvaloniaVS.Navigation
{
    /// <summary>
    /// Ctrl+Click in C#. The editor's Ctrl+Click handler doesn't send a command: it asks
    /// <see cref="INavigableSymbolSource"/>s what's under the mouse (which also drives the hover
    /// underline) and calls <see cref="INavigableSymbol.Navigate"/>. Roslyn's source
    /// ("NavigableSymbolService") would go to the generated <c>x:Name</c> field, so this one runs
    /// first and sends those fields to their XAML element; anything else returns null and falls
    /// through to Roslyn.
    /// </summary>
    [Export(typeof(INavigableSymbolSourceProvider))]
    [Name("Avalonia C# to XAML navigable symbols")]
    [ContentType("CSharp")]
    [Order(Before = "NavigableSymbolService")]
    internal sealed class CSharpXamlNavigableSymbolSourceProvider : INavigableSymbolSourceProvider
    {
        public INavigableSymbolSource TryCreateNavigableSymbolSource(ITextView textView, ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty(() => new Source(buffer));

        private sealed class Source : INavigableSymbolSource
        {
            private readonly ITextBuffer _buffer;

            public Source(ITextBuffer buffer) => _buffer = buffer;

            public async Task<INavigableSymbol> GetNavigableSymbolAsync(SnapshotSpan triggerSpan, CancellationToken token)
            {
                try
                {
                    var backed = await CSharpXamlNavigationCommandHandler.GetXamlBackedSymbolAsync(_buffer, triggerSpan.Start.Position, token);
                    if (backed?.Kind != XamlBackedSymbol.SymbolKind.NamedElement)
                    {
                        return null;
                    }

                    var span = new SnapshotSpan(triggerSpan.Snapshot, backed.Span);
                    return new Symbol(backed, span);
                }
                catch (System.OperationCanceledException)
                {
                    return null;
                }
                catch (System.Exception ex)
                {
                    Log.Logger.Debug(ex, "C# to XAML navigable symbol lookup failed");
                    return null;
                }
            }

            public void Dispose()
            {
            }
        }

        private sealed class Symbol : INavigableSymbol
        {
            private readonly XamlBackedSymbol _backed;

            public Symbol(XamlBackedSymbol backed, SnapshotSpan span)
            {
                _backed = backed;
                SymbolSpan = span;
            }

            public SnapshotSpan SymbolSpan { get; }

            public IEnumerable<INavigableRelationship> Relationships => new[] { PredefinedNavigableRelationships.Definition };

            public void Navigate(INavigableRelationship relationship) =>
                AvaloniaVS.Services.BackgroundWork.Start(
                    () => CSharpXamlNavigationCommandHandler.GoToXamlAsync(_backed),
                    "AvaloniaVS/CSharpCtrlClickToXaml");
        }
    }
}
