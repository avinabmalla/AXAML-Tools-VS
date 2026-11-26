using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Ide.CompletionEngine.Navigation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;

namespace AvaloniaVS.Navigation
{
    /// <summary>A place in a file, with enough detail to show it in Peek or a results list.</summary>
    internal sealed class XamlSourceLocation
    {
        public string Path { get; set; }

        public int Start { get; set; }

        public int Length { get; set; }

        /// <summary>0-based.</summary>
        public int Line { get; set; }

        /// <summary>0-based.</summary>
        public int Column { get; set; }

        public int EndLine { get; set; }

        public int EndColumn { get; set; }

        /// <summary>The trimmed text of the line, for result lists.</summary>
        public string LineText { get; set; }

        public string ProjectName { get; set; }

        public bool IsDefinition { get; set; }

        public static XamlSourceLocation FromText(string path, string text, int start, int length, bool isDefinition, string projectName = null)
        {
            var (line, column) = LineColumn(text, start);
            var (endLine, endColumn) = LineColumn(text, Math.Min(text.Length, start + length));
            var lineStart = start - column;
            var lineEnd = text.IndexOf('\n', lineStart);
            var lineText = text.Substring(lineStart, (lineEnd < 0 ? text.Length : lineEnd) - lineStart).Trim();

            return new XamlSourceLocation
            {
                Path = path,
                Start = start,
                Length = length,
                Line = line,
                Column = column,
                EndLine = endLine,
                EndColumn = endColumn,
                LineText = lineText,
                ProjectName = projectName,
                IsDefinition = isDefinition,
            };
        }

        public static XamlSourceLocation FromRoslyn(Location location, bool isDefinition, string projectName = null)
        {
            var span = location.GetLineSpan();
            var text = location.SourceTree?.GetText();
            var lineText = text is null || span.StartLinePosition.Line >= text.Lines.Count
                ? ""
                : text.Lines[span.StartLinePosition.Line].ToString().Trim();

            return new XamlSourceLocation
            {
                Path = location.SourceTree?.FilePath,
                Start = location.SourceSpan.Start,
                Length = location.SourceSpan.Length,
                Line = span.StartLinePosition.Line,
                Column = span.StartLinePosition.Character,
                EndLine = span.EndLinePosition.Line,
                EndColumn = span.EndLinePosition.Character,
                LineText = lineText,
                ProjectName = projectName,
                IsDefinition = isDefinition,
            };
        }

        private static (int Line, int Column) LineColumn(string text, int offset)
        {
            var line = 0;
            var lineStart = 0;
            for (var i = 0; i < offset && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return (line, offset - lineStart);
        }
    }

    /// <summary>
    /// Resolves XAML navigation targets to definitions (for Peek) and references (for Find All
    /// References): C# locations through Roslyn plus the XAML usages Roslyn can't see.
    /// </summary>
    internal static class XamlLocationService
    {
        /// <summary>The C# symbol a target refers to, or null for XAML-only targets.</summary>
        public static ISymbol ResolveSymbol(XamlRoslynContext context, XamlNavigationTarget target)
        {
            switch (target.Kind)
            {
                case XamlNavigationKind.Type:
                case XamlNavigationKind.XClass:
                    return context.GetType(target.TypeName);
                case XamlNavigationKind.Property:
                    return context.GetType(target.TypeName) is { } owner ? XamlNavigationService.FindPropertyLike(owner, target.MemberName, target.IsAttached) : null;
                case XamlNavigationKind.StaticMember:
                    return context.GetType(target.TypeName) is { } staticOwner ? XamlRoslynContext.FindMembers(staticOwner, target.MemberName).FirstOrDefault() : null;
                case XamlNavigationKind.EventHandler:
                    return context.GetType(target.XClassName) is { } handlerClass
                        ? XamlRoslynContext.FindMembers(handlerClass, target.MemberName).OfType<IMethodSymbol>().FirstOrDefault()
                        : null;
                case XamlNavigationKind.XName:
                    return context.GetType(target.XClassName) is { } nameClass
                        ? XamlRoslynContext.FindMembers(nameClass, target.MemberName).FirstOrDefault(m => m is IFieldSymbol or IPropertySymbol)
                        : null;
                case XamlNavigationKind.BindingPath:
                    return XamlNavigationService.ResolveBindingPath(context, target);
                default:
                    return null;
            }
        }

        /// <summary>Where <paramref name="target"/> is defined, in source (not metadata).</summary>
        public static async Task<IReadOnlyList<XamlSourceLocation>> GetDefinitionsAsync(
            ITextView view, XamlNavigationTarget target, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var xamlPath = XamlRoslynContext.GetFilePath(view.TextBuffer);
            var text = view.TextBuffer.CurrentSnapshot.GetText();
            await TaskScheduler.Default;

            switch (target.Kind)
            {
                case XamlNavigationKind.ResourceKey:
                    return await FindInProjectXamlAsync(xamlPath, text, KeyDefinitionPattern(target.MemberName), isDefinition: true, cancellationToken);

                case XamlNavigationKind.Uri:
                    {
                        var path = await ResolveUriAsync(xamlPath, target.Uri);
                        return path is null
                            ? Array.Empty<XamlSourceLocation>()
                            : new[] { XamlSourceLocation.FromText(path, File.ReadAllText(path), 0, 0, isDefinition: true) };
                    }

                case XamlNavigationKind.XName:
                    {
                        // For a named element, the most useful definition is the element itself.
                        var element = XamlScanner.Scan(text).FirstOrDefault(e => e.ElementName == target.MemberName);
                        var attribute = element?.GetXamlDirective("Name") ?? element?.GetAttribute("Name");
                        if (element != null && attribute != null)
                        {
                            return new[] { XamlSourceLocation.FromText(xamlPath, text, element.Start, element.StartTagEnd - element.Start, isDefinition: true) };
                        }

                        break;
                    }
            }

            var context = await XamlRoslynContext.CreateAsync(xamlPath, cancellationToken);
            var symbol = context is null ? null : ResolveSymbol(context, target);
            return symbol is null ? Array.Empty<XamlSourceLocation>() : SourceDefinitions(context, symbol);
        }

        /// <summary>Definitions and usages of <paramref name="target"/>, C# and XAML.</summary>
        public static async Task<IReadOnlyList<XamlSourceLocation>> FindReferencesAsync(
            ITextView view, XamlNavigationTarget target, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var xamlPath = XamlRoslynContext.GetFilePath(view.TextBuffer);
            var text = view.TextBuffer.CurrentSnapshot.GetText();
            await TaskScheduler.Default;

            var results = new List<XamlSourceLocation>();

            // XAML usages Roslyn doesn't know about.
            results.AddRange(await FindXamlUsagesAsync(
                xamlPath, text, target.Kind, target.MemberName ?? target.Text, target.TypeName ?? target.Text, cancellationToken));

            // C# definitions and references.
            if (target.Kind is not (XamlNavigationKind.ResourceKey or XamlNavigationKind.Uri))
            {
                var context = await XamlRoslynContext.CreateAsync(xamlPath, cancellationToken);
                var symbol = context is null ? null : ResolveSymbol(context, target);
                if (symbol != null)
                {
                    // An x:Name field is declared by the XAML (already in the list); its C#
                    // declaration is generated code nobody wants to land in.
                    results.AddRange(SourceDefinitions(context, handWrittenOnly: target.Kind == XamlNavigationKind.XName, symbol));
                    results.AddRange(await FindCSharpReferencesAsync(context.Solution, symbol, cancellationToken));
                }
            }

            return Normalize(results);
        }

        /// <summary>Sorts definitions first and removes duplicates.</summary>
        public static IReadOnlyList<XamlSourceLocation> Normalize(IEnumerable<XamlSourceLocation> results) =>
            results
                .Where(r => r.Path != null)
                .GroupBy(r => (r.Path.ToUpperInvariant(), r.Line, r.Column))
                .Select(g => g.OrderByDescending(r => r.IsDefinition).First())
                .OrderByDescending(r => r.IsDefinition)
                .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Line)
                .ToList();

        /// <summary>Hand-written C# references to <paramref name="symbol"/> (generated code is skipped).</summary>
        public static async Task<IReadOnlyList<XamlSourceLocation>> FindCSharpReferencesAsync(
            Solution solution, ISymbol symbol, CancellationToken cancellationToken)
        {
            var results = new List<XamlSourceLocation>();
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            {
                foreach (var location in referenced.Locations)
                {
                    if (location.Location.IsInSource && !XamlRoslynContext.IsGenerated(location.Location.SourceTree?.FilePath))
                    {
                        results.Add(XamlSourceLocation.FromRoslyn(location.Location, isDefinition: false, location.Document.Project.Name));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Usages in XAML that Roslyn can't see: named-element references, resource key uses, event
        /// handler attributes and type uses. <paramref name="text"/> is the current (possibly
        /// unsaved) text of <paramref name="xamlPath"/>.
        /// </summary>
        public static async Task<IReadOnlyList<XamlSourceLocation>> FindXamlUsagesAsync(
            string xamlPath, string text, XamlNavigationKind kind, string memberName, string typeName, CancellationToken cancellationToken)
        {
            var results = new List<XamlSourceLocation>();
            var name = Regex.Escape(memberName ?? "");

            switch (kind)
            {
                case XamlNavigationKind.ResourceKey:
                    results.AddRange(await FindInProjectXamlAsync(xamlPath, text, KeyDefinitionPattern(memberName), isDefinition: true, cancellationToken));
                    results.AddRange(await FindInProjectXamlAsync(xamlPath, text,
                        new Regex(@"\{\s*(?:Static|Dynamic)Resource\s+(?:ResourceKey\s*=\s*)?" + name + @"\s*\}"), isDefinition: false, cancellationToken));
                    break;

                case XamlNavigationKind.XName:
                    results.AddRange(FindInText(xamlPath, text, new Regex(@"\b(?:x:)?Name\s*=\s*[""']" + name + @"[""']"), isDefinition: true));
                    results.AddRange(FindInText(xamlPath, text, new Regex(@"(?<=[\s{(=!,])#" + name + @"\b"), isDefinition: false));
                    results.AddRange(FindInText(xamlPath, text, new Regex(@"\bElementName\s*=\s*" + name + @"\b"), isDefinition: false));
                    results.AddRange(FindInText(xamlPath, text, new Regex(@"\{\s*x:Reference\s+" + name + @"\s*\}"), isDefinition: false));
                    break;

                case XamlNavigationKind.EventHandler:
                    results.AddRange(FindInText(xamlPath, text, new Regex(@"\s[\w:.]+\s*=\s*[""']" + name + @"[""']"), isDefinition: false));
                    break;

                case XamlNavigationKind.Type:
                case XamlNavigationKind.XClass:
                    {
                        var local = Regex.Escape((typeName ?? "").Split('.').Last().Split(':').Last());
                        if (local.Length > 0)
                        {
                            results.AddRange(await FindInProjectXamlAsync(xamlPath, text, new Regex(
                                @"<(?:\w+:)?" + local + @"(?=[\s/>.])" +
                                @"|\b(?:x:DataType|DataType|TargetType)\s*=\s*[""'](?:\w+:)?" + local + @"[""']" +
                                @"|\{x:Type\s+(?:\w+:)?" + local + @"\s*\}" +
                                @"|\bx:Class\s*=\s*[""'][\w.]*\b" + local + @"[""']"), isDefinition: false, cancellationToken));
                        }

                        break;
                    }
            }

            return results;
        }

        /// <summary>The symbols' hand-written source locations, falling back to generated ones.</summary>
        public static IReadOnlyList<XamlSourceLocation> SourceDefinitions(XamlRoslynContext context, params ISymbol[] symbols) =>
            SourceDefinitions(context, handWrittenOnly: false, symbols);

        /// <summary>
        /// The symbols' hand-written source locations; generated ones too only when there are none
        /// and <paramref name="handWrittenOnly"/> is false.
        /// </summary>
        public static IReadOnlyList<XamlSourceLocation> SourceDefinitions(XamlRoslynContext context, bool handWrittenOnly, params ISymbol[] symbols)
        {
            var results = new List<XamlSourceLocation>();
            foreach (var symbol in symbols)
            {
                var source = symbol.Locations.Where(l => l.IsInSource).ToList();
                var handWritten = source.Where(l => !XamlRoslynContext.IsGenerated(l.SourceTree?.FilePath)).ToList();
                foreach (var location in handWritten.Count > 0 || handWrittenOnly ? handWritten : source)
                {
                    var project = context.Solution.GetDocument(location.SourceTree)?.Project.Name;
                    results.Add(XamlSourceLocation.FromRoslyn(location, isDefinition: true, project));
                }
            }

            return results;
        }

        internal static async Task<string> ResolveUriAsync(string xamlPath, string uri)
        {
            if (string.IsNullOrWhiteSpace(uri) || xamlPath is null)
            {
                return null;
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
                var rest = value.Substring("avares://".Length);
                var slash = rest.IndexOf('/');
                var assemblyName = slash < 0 ? rest : rest.Substring(0, slash);
                relative = slash < 0 ? "" : rest.Substring(slash + 1);

                var workspace = await XamlRoslynContext.GetWorkspaceAsync();
                var project = workspace.CurrentSolution.Projects.FirstOrDefault(p => string.Equals(p.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase));
                baseDir = project?.FilePath is { } projectPath ? System.IO.Path.GetDirectoryName(projectPath) : null;
            }
            else if (value.StartsWith("/", StringComparison.Ordinal))
            {
                baseDir = await XamlNavigationService.GetProjectDirectoryAsync(xamlPath);
                relative = value.TrimStart('/');
            }
            else
            {
                baseDir = System.IO.Path.GetDirectoryName(xamlPath);
                relative = value;
            }

            if (baseDir is null)
            {
                return null;
            }

            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            return File.Exists(path) ? path : null;
        }

        private static Regex KeyDefinitionPattern(string key) =>
            new Regex(@"\bx:Key\s*=\s*[""']" + Regex.Escape(key ?? "") + @"[""']");

        private static IEnumerable<XamlSourceLocation> FindInText(string path, string text, Regex pattern, bool isDefinition)
        {
            foreach (Match match in pattern.Matches(text))
            {
                yield return XamlSourceLocation.FromText(path, text, match.Index, match.Length, isDefinition);
            }
        }

        /// <summary>Matches in this file (current, unsaved text) and the project's other XAML files.</summary>
        private static async Task<IReadOnlyList<XamlSourceLocation>> FindInProjectXamlAsync(
            string xamlPath, string currentText, Regex pattern, bool isDefinition, CancellationToken cancellationToken)
        {
            var results = FindInText(xamlPath, currentText, pattern, isDefinition).ToList();
            var projectDir = await XamlNavigationService.GetProjectDirectoryAsync(xamlPath);
            if (projectDir is null)
            {
                return results;
            }

            await TaskScheduler.Default;
            foreach (var file in XamlNavigationService.EnumerateXamlFiles(projectDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(file, xamlPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }

                results.AddRange(FindInText(file, text, pattern, isDefinition));
            }

            return results;
        }
    }
}
