using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Serilog;

namespace AvaloniaVS.Navigation
{
    /// <summary>
    /// Creates (or reuses) an event handler method in the code-behind class named by the XAML
    /// file's <c>x:Class</c>.
    /// </summary>
    internal static class EventHandlerGenerator
    {
        public sealed class Request
        {
            /// <summary>The XAML buffer and the span of the event attribute's value in it.</summary>
            public ITextBuffer Buffer { get; set; }

            public ITrackingSpan ValueSpan { get; set; }

            public string XamlPath { get; set; }

            /// <summary>Full name of the <c>x:Class</c>.</summary>
            public string ClassName { get; set; }

            /// <summary>Full name of the type that declares the event.</summary>
            public string EventOwnerType { get; set; }

            public string EventName { get; set; }

            public bool IsAttached { get; set; }

            /// <summary>The handler name inserted into the XAML.</summary>
            public string MethodName { get; set; }

            /// <summary>Open the handler in the C# editor once it exists.</summary>
            public bool Navigate { get; set; }
        }

        /// <summary>
        /// Ensures the handler exists. If the name is already used by an incompatible member, a
        /// numbered name is generated and the XAML attribute is updated to match.
        /// </summary>
        public static async Task<bool> GenerateAsync(Request request, CancellationToken cancellationToken)
        {
            try
            {
                return await GenerateCoreAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Logger.Error(ex, "Failed to generate event handler {Method}", request.MethodName);
                await NavigationHelper.SetStatusAsync($"Couldn't generate the event handler '{request.MethodName}': {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> GenerateCoreAsync(Request request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.ClassName))
            {
                await NavigationHelper.SetStatusAsync("Can't create an event handler: the root element has no x:Class.");
                return false;
            }

            var context = await XamlRoslynContext.CreateAsync(request.XamlPath, cancellationToken);
            var classSymbol = context?.GetType(request.ClassName);
            if (classSymbol is null)
            {
                await NavigationHelper.SetStatusAsync($"Can't create an event handler: class '{request.ClassName}' wasn't found.");
                return false;
            }

            var parameters = ResolveParameters(context, request);
            if (parameters is null)
            {
                await NavigationHelper.SetStatusAsync($"Can't create an event handler: event '{request.EventName}' wasn't found on {request.EventOwnerType}.");
                return false;
            }

            // Reuse an existing compatible handler; pick a free name if the name is taken.
            var methodName = request.MethodName;
            var existing = FindCompatibleHandler(context, classSymbol, methodName, parameters);
            if (existing != null)
            {
                Log.Logger.Debug("Reusing existing handler {Method}", methodName);
                if (request.Navigate)
                {
                    await NavigationHelper.GoToSymbolAsync(context, existing, cancellationToken);
                }

                return true;
            }

            methodName = GetUniqueName(classSymbol, methodName);

            var (document, declaration) = await context.GetCodeBehindDeclarationAsync(classSymbol, cancellationToken);
            if (document is null)
            {
                await NavigationHelper.SetStatusAsync($"Can't create an event handler: no editable source for '{request.ClassName}'.");
                return false;
            }

            var method = BuildMethod(methodName, parameters, context.IsNullableEnabled);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var newRoot = root.ReplaceNode(declaration, declaration.AddMembers(method));
            var newDocument = document.WithSyntaxRoot(newRoot);
            newDocument = await ImportAdder.AddImportsAsync(newDocument, Simplifier.AddImportsAnnotation, cancellationToken: cancellationToken);
            newDocument = await Simplifier.ReduceAsync(newDocument, Simplifier.Annotation, cancellationToken: cancellationToken);
            newDocument = await Formatter.FormatAsync(newDocument, Formatter.Annotation, cancellationToken: cancellationToken);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (!context.Workspace.TryApplyChanges(newDocument.Project.Solution))
            {
                await NavigationHelper.SetStatusAsync($"Couldn't add '{methodName}' to {request.ClassName}: the file may be read-only or changed.");
                return false;
            }

            // The XAML was inserted with the proposed name: fix it up if we had to rename.
            if (methodName != request.MethodName && request.ValueSpan != null)
            {
                var span = request.ValueSpan.GetSpan(request.Buffer.CurrentSnapshot);
                if (span.GetText() == request.MethodName)
                {
                    request.Buffer.Replace(span, methodName);
                }
            }

            Log.Logger.Information("Generated event handler {Class}.{Method}", request.ClassName, methodName);

            if (request.Navigate)
            {
                await NavigateIntoMethodAsync(context, newDocument.Id, methodName, cancellationToken);
            }

            return true;
        }

        /// <summary>
        /// Gets the handler parameters for the event: the delegate's parameters for a CLR event,
        /// or <c>(object sender, TArgs e)</c> for a routed/attached event registered as
        /// <c>RoutedEvent&lt;TArgs&gt;</c>.
        /// </summary>
        internal static IReadOnlyList<(ITypeSymbol Type, string Name)> ResolveParameters(XamlRoslynContext context, Request request) =>
            ResolveParameters(context, request.EventOwnerType, request.EventName, request.IsAttached);

        internal static IReadOnlyList<(ITypeSymbol Type, string Name)> ResolveParameters(
            XamlRoslynContext context, string eventOwnerType, string eventName, bool isAttached)
        {
            var owner = context.GetType(eventOwnerType);
            if (owner is null)
            {
                return null;
            }

            if (!isAttached)
            {
                var clrEvent = XamlRoslynContext.FindMembers(owner, eventName).OfType<IEventSymbol>().FirstOrDefault();
                if (clrEvent?.Type is INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
                {
                    return invoke.Parameters.Select(p => (p.Type, p.Name)).ToList();
                }
            }

            var field = XamlRoslynContext.FindMembers(owner, eventName + "Event").OfType<IFieldSymbol>().FirstOrDefault();
            if (field?.Type is INamedTypeSymbol fieldType)
            {
                var args = fieldType.TypeArguments.FirstOrDefault()
                    ?? context.GetType("Avalonia.Interactivity.RoutedEventArgs");
                if (args != null)
                {
                    return new[] { ((ITypeSymbol)context.Compilation.GetSpecialType(SpecialType.System_Object), "sender"), (args, "e") };
                }
            }

            return null;
        }

        private static IMethodSymbol FindCompatibleHandler(
            XamlRoslynContext context, INamedTypeSymbol type, string name, IReadOnlyList<(ITypeSymbol Type, string Name)> parameters) =>
            type.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(m => IsCompatibleHandler(context, m, parameters));

        /// <summary>
        /// Methods on <paramref name="type"/> (not its bases) that can handle an event with these
        /// parameters, for listing in completion.
        /// </summary>
        internal static IEnumerable<IMethodSymbol> FindCompatibleHandlers(
            XamlRoslynContext context, INamedTypeSymbol type, IReadOnlyList<(ITypeSymbol Type, string Name)> parameters) =>
            type.GetMembers().OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic && !m.IsImplicitlyDeclared)
                .Where(m => IsCompatibleHandler(context, m, parameters));

        /// <summary>
        /// A void method whose parameters accept the event's arguments. Taking a base type of the
        /// event args (e.g. RoutedEventArgs for TappedEventArgs) is fine.
        /// </summary>
        internal static bool IsCompatibleHandler(
            XamlRoslynContext context, IMethodSymbol method, IReadOnlyList<(ITypeSymbol Type, string Name)> parameters)
        {
            if (!method.ReturnsVoid || method.IsGenericMethod || method.Parameters.Length != parameters.Count)
            {
                return false;
            }

            for (var i = 0; i < parameters.Count; i++)
            {
                var conversion = context.Compilation.ClassifyConversion(parameters[i].Type, method.Parameters[i].Type);
                if (!conversion.IsIdentity && !conversion.IsImplicit)
                {
                    return false;
                }
            }

            return true;
        }

        private static string GetUniqueName(INamedTypeSymbol type, string name)
        {
            var taken = new HashSet<string>(type.GetMembers().Select(m => m.Name), StringComparer.Ordinal);
            if (!taken.Contains(name))
            {
                return name;
            }

            for (var i = 2; ; i++)
            {
                var candidate = $"{name}_{i}";
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        private static MethodDeclarationSyntax BuildMethod(string name, IReadOnlyList<(ITypeSymbol Type, string Name)> parameters, bool nullable)
        {
            var parameterList = parameters.Select(p =>
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
                    .WithType(TypeSyntaxFor(p.Type, nullable)));

            return SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    SyntaxFactory.Identifier(name))
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameterList)))
                .WithBody(SyntaxFactory.Block())
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static TypeSyntax TypeSyntaxFor(ITypeSymbol type, bool nullable)
        {
            // `sender` is the one reference-type parameter that's legitimately nullable.
            if (type.SpecialType == SpecialType.System_Object)
            {
                TypeSyntax obj = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
                return nullable ? SyntaxFactory.NullableType(obj) : obj;
            }

            var text = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return SyntaxFactory.ParseTypeName(text)
                .WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);
        }

        private static async Task NavigateIntoMethodAsync(XamlRoslynContext context, DocumentId documentId, string methodName, CancellationToken cancellationToken)
        {
            var document = context.Workspace.CurrentSolution.GetDocument(documentId);
            var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken);
            var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().LastOrDefault(m => m.Identifier.Text == methodName);
            if (method?.Body is { } body && document.FilePath is { } path)
            {
                await NavigationHelper.OpenAtAsync(path, body.OpenBraceToken.Span.End);
            }
        }
    }
}
