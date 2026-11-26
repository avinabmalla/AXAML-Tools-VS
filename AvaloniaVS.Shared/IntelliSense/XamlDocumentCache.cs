using Avalonia.Ide.CompletionEngine.Navigation;
using Microsoft.VisualStudio.Text;

namespace AvaloniaVS.IntelliSense
{
    /// <summary>
    /// The scanned <see cref="XamlDocument"/> for a buffer's snapshot, parsed at most once per edit
    /// and shared by indent guides, binding checks, navigation and indentation.
    /// </summary>
    internal sealed class XamlDocumentCache
    {
        private readonly object _gate = new object();
        private ITextSnapshot _snapshot;
        private XamlDocument _document;

        public static XamlDocument Get(ITextSnapshot snapshot) =>
            snapshot.TextBuffer.Properties
                .GetOrCreateSingletonProperty(() => new XamlDocumentCache())
                .GetDocument(snapshot);

        private XamlDocument GetDocument(ITextSnapshot snapshot)
        {
            lock (_gate)
            {
                if (_snapshot == snapshot)
                {
                    return _document;
                }
            }

            // Parse outside the lock; a racing caller for the same snapshot just parses too.
            var document = XamlDocument.Parse(snapshot.GetText());

            lock (_gate)
            {
                // Keep the newest snapshot so an older request doesn't evict it.
                if (_snapshot is null || snapshot.Version.VersionNumber >= _snapshot.Version.VersionNumber)
                {
                    _snapshot = snapshot;
                    _document = document;
                }
            }

            return document;
        }
    }
}
