using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Ide.CompletionEngine;
using Avalonia.Ide.CompletionEngine.Navigation;
using AvaloniaVS.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Serilog;

namespace AvaloniaVS.Navigation
{
    /// <summary>
    /// Go To Definition / Go To Implementation for Avalonia XAML.
    /// </summary>
    internal static class XamlNavigationService
    {
        /// <summary>
        /// Classifies the position in <paramref name="view"/>'s buffer. Cheap enough to run on the
        /// UI thread for a single command or click.
        /// </summary>
        public static XamlNavigationTarget Classify(ITextView view, SnapshotPoint point)
        {
            var buffer = point.Snapshot.TextBuffer;
            var document = IntelliSense.XamlDocumentCache.Get(point.Snapshot);
            var text = document.Text;
            CompletionEngine.MetadataHelper helper = null;

            if (buffer.Properties.TryGetProperty(typeof(XamlBufferMetadata), out XamlBufferMetadata metadata) &&
                metadata.CompletionMetadata is { } completionMetadata)
            {
                buffer.Properties.TryGetProperty("AssemblyName", out string assemblyName);
                helper = new CompletionEngine.MetadataHelper();
                helper.SetMetadata(completionMetadata, text, assemblyName);
            }

            return XamlNavigationClassifier.Classify(document, point.Position, helper);
        }

        /// <summary>Navigates to <paramref name="target"/>. Reports failures in the status bar.</summary>
        public static async Task NavigateAsync(ITextView view, XamlNavigationTarget target, bool implementation, CancellationToken cancellationToken)
        {
            try
            {
                if (!await NavigateCoreAsync(view, target, implementation, cancellationToken))
                {
                    await NavigationHelper.SetStatusAsync(GetFailureMessage(target));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Logger.Error(ex, "XAML navigation failed for {Target}", target);
                await NavigationHelper.SetStatusAsync($"Couldn't navigate to '{target.Text}': {ex.Message}");
            }
        }

        /// <summary>Shift+F12: lists definitions and usages (C# and XAML) in the references window.</summary>
        public static async Task FindAllReferencesAsync(ITextView view, XamlNavigationTarget target, CancellationToken cancellationToken)
        {
            try
            {
                await NavigationHelper.SetStatusAsync($"Finding references to '{target.Text}'\u2026");
                var locations = await XamlLocationService.FindReferencesAsync(view, target, cancellationToken);
                if (locations.Count == 0)
                {
                    await NavigationHelper.SetStatusAsync($"No references to '{target.Text}' were found.");
                    return;
                }

                await ReferencesWindow.ShowAsync($"'{target.Text}' references", locations);
                await NavigationHelper.SetStatusAsync($"{locations.Count} result(s) for '{target.Text}'.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Logger.Error(ex, "Find All References failed for {Target}", target);
                await NavigationHelper.SetStatusAsync($"Couldn't find references to '{target.Text}': {ex.Message}");
            }
        }

        private static async Task<bool> NavigateCoreAsync(ITextView view, XamlNavigationTarget target, bool implementation, CancellationToken ct)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var xamlPath = XamlRoslynContext.GetFilePath(view.TextBuffer);

            // Targets that stay in XAML don't need a compilation.
            switch (target.Kind)
            {
                case XamlNavigationKind.ResourceKey:
                    return await GoToResourceAsync(view, xamlPath, target.MemberName);
                case XamlNavigationKind.Uri:
                    return await GoToUriAsync(xamlPath, target.Uri);
            }

            var context = await XamlRoslynContext.CreateAsync(xamlPath, ct);
            if (context is null)
            {
                return false;
            }

            switch (target.Kind)
            {
                case XamlNavigationKind.Type:
                case XamlNavigationKind.XClass:
                    {
                        var type = context.GetType(target.TypeName);
                        if (type is null)
                        {
                            return false;
                        }

                        if (implementation && await GoToImplementationsAsync(context, type, ct))
                        {
                            return true;
                        }

                        if (target.Kind == XamlNavigationKind.XClass || type.Locations.Any(l => l.IsInSource))
                        {
                            // Prefer the code-behind partial over the generated one.
                            var (document, declaration) = await context.GetCodeBehindDeclarationAsync(type, ct);
                            if (document?.FilePath is { } path && !XamlRoslynContext.IsGenerated(path))
                            {
                                return await NavigationHelper.OpenAtAsync(path, declaration.Identifier.SpanStart);
                            }
                        }

                        return await NavigationHelper.GoToSymbolAsync(context, type, ct);
                    }

                case XamlNavigationKind.Property:
                    {
                        var owner = context.GetType(target.TypeName);
                        var member = owner is null ? null : FindPropertyLike(owner, target.MemberName, target.IsAttached);
                        return member != null && await GoToMemberAsync(context, member, implementation, ct);
                    }

                case XamlNavigationKind.StaticMember:
                    {
                        var owner = context.GetType(target.TypeName);
                        var member = owner is null ? null : XamlRoslynContext.FindMembers(owner, target.MemberName).FirstOrDefault();
                        return member != null && await GoToMemberAsync(context, member, implementation, ct);
                    }

                case XamlNavigationKind.EventHandler:
                    return await GoToEventHandlerAsync(view, context, target, ct);

                case XamlNavigationKind.XName:
                    return await GoToNamedElementAsync(view, context, target, ct);

                case XamlNavigationKind.BindingPath:
                    {
                        var member = ResolveBindingPath(context, target);
                        return member != null && await GoToMemberAsync(context, member, implementation, ct);
                    }

                default:
                    return false;
            }
        }

        private static async Task<bool> GoToMemberAsync(XamlRoslynContext context, ISymbol member, bool implementation, CancellationToken ct)
        {
            if (implementation)
            {
                var implementations = (await SymbolFinder.FindImplementationsAsync(member, context.Solution, cancellationToken: ct)).ToList();
                if (member.IsVirtual || member.IsAbstract)
                {
                    implementations.AddRange(await SymbolFinder.FindOverridesAsync(member, context.Solution, cancellationToken: ct));
                }

                var inSource = implementations.Where(s => s.Locations.Any(l => l.IsInSource)).ToList();
                if (inSource.Count == 1)
                {
                    return await NavigationHelper.GoToSymbolAsync(context, inSource[0], ct);
                }

                if (inSource.Count > 1)
                {
                    // Several: let the user pick from the results window.
                    await ReferencesWindow.ShowAsync(
                        $"Implementations of '{member.Name}'",
                        XamlLocationService.SourceDefinitions(context, inSource.ToArray()));
                    return true;
                }
            }

            return await NavigationHelper.GoToSymbolAsync(context, member, ct);
        }

        private static async Task<bool> GoToImplementationsAsync(XamlRoslynContext context, INamedTypeSymbol type, CancellationToken ct)
        {
            IEnumerable<INamedTypeSymbol> found = type.TypeKind == TypeKind.Interface
                ? await SymbolFinder.FindImplementationsAsync(type, context.Solution, cancellationToken: ct)
                : await SymbolFinder.FindDerivedClassesAsync(type, context.Solution, cancellationToken: ct);

            var inSource = found.Where(t => t.Locations.Any(l => l.IsInSource)).ToList();
            if (inSource.Count == 0)
            {
                return false;
            }

            if (inSource.Count > 1)
            {
                // Several: let the user pick from the results window.
                await ReferencesWindow.ShowAsync(
                    type.TypeKind == TypeKind.Interface ? $"Implementations of '{type.Name}'" : $"Types deriving from '{type.Name}'",
                    XamlLocationService.SourceDefinitions(context, inSource.ToArray()));
                return true;
            }

            return await NavigationHelper.GoToSymbolAsync(context, inSource[0], ct);
        }

        /// <summary>
        /// Finds what a XAML property name refers to. CLR properties come first; for attached
        /// properties the registration field (<c>RowProperty</c>) is more useful than the accessors.
        /// </summary>
        internal static ISymbol FindPropertyLike(INamedTypeSymbol owner, string name, bool attached)
        {
            IEnumerable<string> candidates = attached
                ? new[] { name + "Property", name + "Event", "Get" + name, name }
                : new[] { name, name + "Property", name + "Event" };

            foreach (var candidate in candidates)
            {
                var member = XamlRoslynContext.FindMembers(owner, candidate)
                    .FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol or IEventSymbol or IMethodSymbol);
                if (member != null)
                {
                    return member;
                }
            }

            return null;
        }

        private static async Task<bool> GoToEventHandlerAsync(ITextView view, XamlRoslynContext context, XamlNavigationTarget target, CancellationToken ct)
        {
            var type = context.GetType(target.XClassName);
            if (type is null)
            {
                return false;
            }

            var method = XamlRoslynContext.FindMembers(type, target.MemberName).OfType<IMethodSymbol>().FirstOrDefault();
            if (method != null)
            {
                return await NavigationHelper.GoToSymbolAsync(context, method, ct);
            }

            // The handler doesn't exist yet: offer to create it.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var answer = VsShellUtilities.ShowMessageBox(
                ServiceProvider.GlobalProvider,
                $"'{target.MemberName}' doesn't exist in {type.Name}. Create it?",
                "Avalonia XAML Tools",
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            if (answer != (int)VSConstants.MessageBoxResult.IDYES)
            {
                return true; // Declined: not a failure worth reporting.
            }

            var snapshot = view.TextBuffer.CurrentSnapshot;
            return await EventHandlerGenerator.GenerateAsync(new EventHandlerGenerator.Request
            {
                Buffer = view.TextBuffer,
                ValueSpan = snapshot.CreateTrackingSpan(target.Start, target.Length, SpanTrackingMode.EdgeExclusive),
                XamlPath = context.XamlPath,
                ClassName = target.XClassName,
                EventOwnerType = target.EventOwnerTypeName,
                EventName = target.EventName,
                IsAttached = target.IsAttached,
                MethodName = target.MemberName,
                Navigate = true,
            }, ct);
        }

        /// <summary>
        /// For <c>x:Name</c>, the generated field is rarely what you want: go to the first use of
        /// it in hand-written code, falling back to the field, then to the element in XAML.
        /// </summary>
        private static async Task<bool> GoToNamedElementAsync(ITextView view, XamlRoslynContext context, XamlNavigationTarget target, CancellationToken ct)
        {
            var type = context.GetType(target.XClassName);
            var field = type is null ? null : XamlRoslynContext.FindMembers(type, target.MemberName).FirstOrDefault(m => m is IFieldSymbol or IPropertySymbol);

            if (field != null)
            {
                var declared = XamlRoslynContext.GetBestLocation(field);
                if (declared != null && !XamlRoslynContext.IsGenerated(declared.SourceTree?.FilePath))
                {
                    return await NavigationHelper.OpenAtAsync(declared);
                }

                var references = await SymbolFinder.FindReferencesAsync(field, context.Solution, ct);
                var usage = references
                    .SelectMany(r => r.Locations)
                    .Select(l => l.Location)
                    .Where(l => l.IsInSource && !XamlRoslynContext.IsGenerated(l.SourceTree?.FilePath))
                    .OrderBy(l => l.SourceTree.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(l => l.SourceSpan.Start)
                    .FirstOrDefault();

                if (usage != null)
                {
                    return await NavigationHelper.OpenAtAsync(usage);
                }

                // Only declared in generated code: don't send the user there (the XAML is the
                // real declaration).
            }

            // From a binding's #name: jump to the element that declares it.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var text = view.TextBuffer.CurrentSnapshot.GetText();
            var element = XamlScanner.Scan(text).FirstOrDefault(e => e.ElementName == target.MemberName);
            var attribute = element?.GetXamlDirective("Name") ?? element?.GetAttribute("Name");
            if (attribute != null && attribute.ValueStart != target.Start)
            {
                NavigationHelper.MoveCaret(view, attribute.ValueStart);
                return true;
            }

            if (attribute != null)
            {
                // Already on the declaration and nothing in code uses it.
                await NavigationHelper.SetStatusAsync($"'{target.MemberName}' is declared here and isn't used in code yet.");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Walks a binding path from its data type. A <c>FooCommand</c> generated by
        /// CommunityToolkit's <c>[RelayCommand]</c> resolves to the <c>Foo</c> method.
        /// </summary>
        internal static ISymbol ResolveBindingPath(XamlRoslynContext context, XamlNavigationTarget target)
        {
            ITypeSymbol current = context.GetType(target.DataTypeName);
            ISymbol member = null;

            for (var i = 0; i <= target.PathIndex && i < target.PathSegments.Count && current != null; i++)
            {
                var name = target.PathSegments[i];
                member = XamlRoslynContext.FindMembers(current, name)
                    .FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol or IMethodSymbol);

                var isLast = i == target.PathIndex;
                if (isLast && name.EndsWith("Command", StringComparison.Ordinal) &&
                    (member is null || XamlRoslynContext.IsGenerated(XamlRoslynContext.GetBestLocation(member)?.SourceTree?.FilePath)))
                {
                    var stem = name.Substring(0, name.Length - "Command".Length);
                    var commandMethod = XamlRoslynContext.FindMembers(current, stem)
                        .Concat(XamlRoslynContext.FindMembers(current, stem + "Async"))
                        .OfType<IMethodSymbol>()
                        .FirstOrDefault(m => m.GetAttributes().Any(a => a.AttributeClass?.Name is "RelayCommandAttribute" or "RelayCommand"));
                    if (commandMethod != null)
                    {
                        return commandMethod;
                    }
                }

                if (member is null)
                {
                    return null;
                }

                current = member switch
                {
                    IPropertySymbol p => p.Type,
                    IFieldSymbol f => f.Type,
                    _ => null,
                };

                // Nullable<T> paths bind through to T.
                if (current is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                {
                    current = nullable.TypeArguments[0];
                }
            }

            return member;
        }

        private static async Task<bool> GoToResourceAsync(ITextView view, string xamlPath, string key)
        {
            var pattern = new Regex(@"\bx:Key\s*=\s*[""']" + Regex.Escape(key) + @"[""']");

            // This file first.
            var match = pattern.Match(view.TextBuffer.CurrentSnapshot.GetText());
            if (match.Success)
            {
                NavigationHelper.MoveCaret(view, match.Index);
                return true;
            }

            // Then the other XAML files in the project (App.axaml, resource dictionaries, styles).
            var projectDir = await GetProjectDirectoryAsync(xamlPath);
            if (projectDir is null)
            {
                return false;
            }

            var hit = await Task.Run(() =>
            {
                foreach (var file in EnumerateXamlFiles(projectDir))
                {
                    if (string.Equals(file, xamlPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var m = pattern.Match(File.ReadAllText(file));
                    if (m.Success)
                    {
                        return (file, m.Index);
                    }
                }

                return (null, 0);
            });

            return hit.Item1 != null && await NavigationHelper.OpenAtAsync(hit.Item1, hit.Item2);
        }

        private static async Task<bool> GoToUriAsync(string xamlPath, string uri)
        {
            if (string.IsNullOrWhiteSpace(uri) || xamlPath is null)
            {
                return false;
            }

            var value = uri.Trim();
            var query = value.IndexOfAny(new[] { '?', '#' });
            if (query > 0)
            {
                value = value.Substring(0, query);
            }

            string baseDir;
            string relative;

            if (value.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            {
                // avares://AssemblyName/path/to/file.axaml
                var rest = value.Substring("avares://".Length);
                var slash = rest.IndexOf('/');
                var assemblyName = slash < 0 ? rest : rest.Substring(0, slash);
                relative = slash < 0 ? "" : rest.Substring(slash + 1);

                var workspace = await XamlRoslynContext.GetWorkspaceAsync();
                var project = workspace.CurrentSolution.Projects.FirstOrDefault(p => string.Equals(p.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase));
                baseDir = project?.FilePath is { } projectPath ? Path.GetDirectoryName(projectPath) : null;
            }
            else if (value.StartsWith("/", StringComparison.Ordinal))
            {
                baseDir = await GetProjectDirectoryAsync(xamlPath);
                relative = value.TrimStart('/');
            }
            else
            {
                baseDir = Path.GetDirectoryName(xamlPath);
                relative = value;
            }

            if (baseDir is null)
            {
                return false;
            }

            var path = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(path) && await NavigationHelper.OpenAtAsync(path, 0);
        }

        internal static async Task<string> GetProjectDirectoryAsync(string xamlPath)
        {
            var workspace = await XamlRoslynContext.GetWorkspaceAsync();
            var project = XamlRoslynContext.FindProject(workspace.CurrentSolution, xamlPath);
            return project?.FilePath is { } path ? Path.GetDirectoryName(path) : null;
        }

        internal static IEnumerable<string> EnumerateXamlFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] files;
                string[] dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    if (file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return file;
                    }
                }

                foreach (var sub in dirs)
                {
                    var name = Path.GetFileName(sub);
                    if (!name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("obj", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith(".", StringComparison.Ordinal) &&
                        !name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Push(sub);
                    }
                }
            }
        }

        private static string GetFailureMessage(XamlNavigationTarget target) => target.Kind switch
        {
            XamlNavigationKind.Type when target.TypeName is null =>
                $"Couldn't resolve type '{target.Text}'. Build the project so Avalonia XAML Tools can load its metadata.",
            XamlNavigationKind.BindingPath when target.DataTypeName is null =>
                $"Can't resolve '{target.Text}': no x:DataType is in scope for this binding.",
            XamlNavigationKind.ResourceKey => $"Resource '{target.Text}' wasn't found in this project's XAML files.",
            XamlNavigationKind.Uri => $"File '{target.Text}' wasn't found.",
            _ => $"Couldn't find a definition for '{target.Text}'.",
        };
    }
}
