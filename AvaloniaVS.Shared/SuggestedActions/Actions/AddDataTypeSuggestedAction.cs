using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Ide.CompletionEngine;
using Avalonia.Ide.CompletionEngine.Navigation;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;

namespace AvaloniaVS.Shared.SuggestedActions.Actions
{
    /// <summary>
    /// Adds <c>x:DataType="vm:SomeViewModel"</c> to the element that owns a binding's data
    /// context (the root, or the enclosing DataTemplate), declaring the xmlns if needed.
    /// </summary>
    internal sealed class AddDataTypeSuggestedAction : ISuggestedAction
    {
        private readonly ITextBuffer _buffer;
        private readonly ITextSnapshot _snapshot;
        private readonly XamlElement _owner;
        private readonly XamlElement _root;
        private readonly string _typeName;
        private readonly string _clrNamespace;
        private readonly string _prefix;
        private readonly bool _addXmlns;

        private AddDataTypeSuggestedAction(ITextBuffer buffer, ITextSnapshot snapshot, XamlElement owner, XamlElement root,
            string typeName, string clrNamespace, string prefix, bool addXmlns)
        {
            _buffer = buffer;
            _snapshot = snapshot;
            _owner = owner;
            _root = root;
            _typeName = typeName;
            _clrNamespace = clrNamespace;
            _prefix = prefix;
            _addXmlns = addXmlns;
        }

        public string DisplayText => _owner == _root
            ? $"Add x:DataType=\"{_prefix}:{_typeName}\""
            : $"Add x:DataType=\"{_prefix}:{_typeName}\" to <{_owner.Name}>";

        public bool HasActionSets => false;

        public bool HasPreview => false;

        public ImageMoniker IconMoniker => default;

        public string IconAutomationText => null;

        public string InputGestureText => null;

        /// <summary>
        /// Creates up to <paramref name="max"/> actions for the binding, one per likely view model
        /// type in the current project, best guess first.
        /// </summary>
        /// <param name="inverseNamespace">Type full name → xmlns, from the completion metadata.</param>
        public static IEnumerable<ISuggestedAction> Create(
            ITextBuffer buffer, BindingWithoutDataType binding, IReadOnlyDictionary<string, string> inverseNamespace, int max = 5)
        {
            var snapshot = buffer.CurrentSnapshot;
            var text = snapshot.GetText();
            var elements = XamlScanner.Scan(text);
            var root = XamlScanner.GetRoot(elements);
            if (root is null || inverseNamespace is null)
            {
                return Array.Empty<ISuggestedAction>();
            }

            // Re-find the owner in this snapshot (the analyzer's elements came from the same text).
            var owner = elements.FirstOrDefault(e => e.Start == binding.SuggestedDataTypeOwner.Start) ?? root;
            var aliases = CompletionEngine.GetNamespaceAliases(text);
            var className = root.GetXamlDirective("Class")?.Value?.Split('.').Last() ?? "";
            var ownerHint = owner == root ? className : "";

            var candidates = inverseNamespace
                .Where(kv => (kv.Value.StartsWith("clr-namespace:", StringComparison.Ordinal) || kv.Value.StartsWith("using:", StringComparison.Ordinal))
                    && kv.Key.EndsWith("ViewModel", StringComparison.Ordinal)
                    && kv.Key.IndexOf('`') < 0 && kv.Key.IndexOf('+') < 0)
                .Select(kv => (FullName: kv.Key, Name: kv.Key.Substring(kv.Key.LastIndexOf('.') + 1), Namespace: XamlTypeNameResolver.GetClrNamespace(kv.Value)))
                .Where(c => c.Namespace != null)
                .OrderBy(c => Rank(c.Name, ownerHint))
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .Take(max)
                .ToList();

            return candidates.Select(c =>
            {
                var existing = aliases.FirstOrDefault(a => a.Key.Length > 0 && XamlTypeNameResolver.GetClrNamespace(a.Value) == c.Namespace).Key;
                var prefix = existing ?? FreePrefix(aliases, "vm");
                return (ISuggestedAction)new AddDataTypeSuggestedAction(buffer, snapshot, owner, root, c.Name, c.Namespace, prefix, existing is null);
            }).ToList();
        }

        public void Invoke(CancellationToken cancellationToken)
        {
            if (_buffer.CurrentSnapshot != _snapshot)
            {
                return; // The document changed since the light bulb was shown.
            }

            using var edit = _buffer.CreateEdit();
            edit.Insert(_owner.NameEnd, $" x:DataType=\"{_prefix}:{_typeName}\"");

            if (_addXmlns)
            {
                edit.Insert(_root.NameEnd, $" xmlns:{_prefix}=\"using:{_clrNamespace}\"");
            }

            edit.Apply();
        }

        public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<SuggestedActionSet>());

        public Task<object> GetPreviewAsync(CancellationToken cancellationToken) => Task.FromResult<object>(null);

        public bool TryGetTelemetryId(out Guid telemetryId)
        {
            telemetryId = Guid.Empty;
            return false;
        }

        public void Dispose()
        {
        }

        /// <summary>MainWindow → MainWindowViewModel first, then names sharing a prefix.</summary>
        private static int Rank(string name, string hint)
        {
            if (hint.Length == 0)
            {
                return 2;
            }

            if (name == hint + "ViewModel")
            {
                return 0;
            }

            var stem = hint.EndsWith("View", StringComparison.Ordinal) ? hint.Substring(0, hint.Length - 4)
                : hint.EndsWith("Window", StringComparison.Ordinal) ? hint.Substring(0, hint.Length - 6)
                : hint;
            return stem.Length > 0 && name.StartsWith(stem, StringComparison.Ordinal) ? 1 : 2;
        }

        private static string FreePrefix(Dictionary<string, string> aliases, string baseName)
        {
            if (!aliases.ContainsKey(baseName))
            {
                return baseName;
            }

            for (var i = 2; ; i++)
            {
                if (!aliases.ContainsKey(baseName + i))
                {
                    return baseName + i;
                }
            }
        }
    }
}
