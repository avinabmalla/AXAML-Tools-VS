using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;

namespace AvaloniaVS.Navigation
{
    /// <summary>
    /// The Roslyn view of a XAML file: the project that compiles it, its compilation, and
    /// helpers to find types, members and the code-behind class.
    /// </summary>
    internal sealed class XamlRoslynContext
    {
        private XamlRoslynContext(VisualStudioWorkspace workspace, Project project, Compilation compilation, string xamlPath)
        {
            Workspace = workspace;
            Project = project;
            Compilation = compilation;
            XamlPath = xamlPath;
        }

        public VisualStudioWorkspace Workspace { get; }

        public Project Project { get; }

        public Solution Solution => Project.Solution;

        public Compilation Compilation { get; }

        public string XamlPath { get; }

        public bool IsNullableEnabled => Compilation.Options.NullableContextOptions != NullableContextOptions.Disable;

        public static async Task<VisualStudioWorkspace> GetWorkspaceAsync()
        {
            var componentModel = (IComponentModel)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SComponentModel));
            return componentModel.GetService<VisualStudioWorkspace>();
        }

        public static string GetFilePath(ITextBuffer buffer) =>
            buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document) ? document.FilePath : null;

        /// <summary>
        /// Creates the context for <paramref name="xamlPath"/>, or returns null if no C# project in
        /// the solution contains it.
        /// </summary>
        public static async Task<XamlRoslynContext> CreateAsync(string xamlPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(xamlPath))
            {
                return null;
            }

            var workspace = await GetWorkspaceAsync();
            var project = FindProject(workspace.CurrentSolution, xamlPath);
            if (project is null)
            {
                return null;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            return compilation is null ? null : new XamlRoslynContext(workspace, project, compilation, xamlPath);
        }

        /// <summary>
        /// Finds the project for a XAML file: one that lists it as a document (Avalonia passes
        /// .axaml files to the compiler as AdditionalFiles), otherwise the C# project whose folder
        /// contains it most closely.
        /// </summary>
        public static Project FindProject(Solution solution, string xamlPath)
        {
            var byDocument = solution.GetDocumentIdsWithFilePath(xamlPath)
                .Select(id => solution.GetProject(id.ProjectId))
                .Where(p => p?.Language == LanguageNames.CSharp)
                .OrderByDescending(ReferencesAvalonia)
                .FirstOrDefault();

            if (byDocument != null)
            {
                return byDocument;
            }

            var fullPath = Path.GetFullPath(xamlPath);
            return solution.Projects
                .Where(p => p.Language == LanguageNames.CSharp && !string.IsNullOrEmpty(p.FilePath))
                .Select(p => (Project: p, Dir: EnsureTrailingSlash(Path.GetDirectoryName(p.FilePath))))
                .Where(x => fullPath.StartsWith(x.Dir, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Dir.Length)
                .ThenByDescending(x => ReferencesAvalonia(x.Project))
                .Select(x => x.Project)
                .FirstOrDefault();
        }

        /// <summary>
        /// Looks up a type by full CLR name in this compilation and, if that's ambiguous or missing,
        /// in each referenced assembly.
        /// </summary>
        public INamedTypeSymbol GetType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return null;
            }

            var type = Compilation.GetTypeByMetadataName(fullName);
            if (type != null)
            {
                return type;
            }

            foreach (var reference in Compilation.References)
            {
                if (Compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly &&
                    assembly.GetTypeByMetadataName(fullName) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// Finds a member by name on <paramref name="type"/> or its base types (and, for
        /// interfaces, the interfaces it extends).
        /// </summary>
        public static IEnumerable<ISymbol> FindMembers(ITypeSymbol type, string name)
        {
            var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var queue = new Queue<ITypeSymbol>();
            queue.Enqueue(type);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is null || !seen.Add(current))
                {
                    continue;
                }

                foreach (var member in current.GetMembers(name))
                {
                    yield return member;
                }

                if (current.BaseType != null)
                {
                    queue.Enqueue(current.BaseType);
                }

                if (current.TypeKind == TypeKind.Interface)
                {
                    foreach (var parent in current.Interfaces)
                    {
                        queue.Enqueue(parent);
                    }
                }
            }
        }

        /// <summary>
        /// Gets the hand-written partial declaration of the code-behind class: the one in
        /// <c>Foo.axaml.cs</c> next to the XAML if it exists, otherwise any declaration that
        /// isn't in generated code.
        /// </summary>
        public async Task<(Document Document, ClassDeclarationSyntax Declaration)> GetCodeBehindDeclarationAsync(
            INamedTypeSymbol type, CancellationToken cancellationToken)
        {
            var expected = XamlPath + ".cs";
            var candidates = new List<(Document, ClassDeclarationSyntax, int)>();

            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                var document = Solution.GetDocument(reference.SyntaxTree);
                if (document is null || await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false) is not ClassDeclarationSyntax declaration)
                {
                    continue;
                }

                var path = reference.SyntaxTree.FilePath ?? "";
                var rank = string.Equals(path, expected, StringComparison.OrdinalIgnoreCase) ? 0 : IsGenerated(path) ? 2 : 1;
                candidates.Add((document, declaration, rank));
            }

            var best = candidates.OrderBy(c => c.Item3).FirstOrDefault();
            return best.Item1 is null ? default : (best.Item1, best.Item2);
        }

        /// <summary>Whether a file is generated code (source generators, obj/ output, .g.cs).</summary>
        public static bool IsGenerated(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }

            var normalized = path.Replace('/', '\\');
            return normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
                || normalized.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0
                || !File.Exists(path);
        }

        /// <summary>Source locations in files a user would recognise, falling back to generated ones.</summary>
        public static Location GetBestLocation(ISymbol symbol)
        {
            var source = symbol.Locations.Where(l => l.IsInSource).ToList();
            return source.FirstOrDefault(l => !IsGenerated(l.SourceTree?.FilePath)) ?? source.FirstOrDefault();
        }

        private static bool ReferencesAvalonia(Project project) =>
            project.MetadataReferences.Any(r => r.Display?.IndexOf("Avalonia", StringComparison.OrdinalIgnoreCase) >= 0);

        private static string EnsureTrailingSlash(string dir) =>
            dir.EndsWith("\\", StringComparison.Ordinal) ? dir : dir + "\\";
    }
}
