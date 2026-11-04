using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetQuery.SourceGenerators;

/// <summary>Generates query keys, query definitions and mutations for interfaces and classes marked with <c>[GenerateQueries]</c>.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class QueriesGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // [GenerateQueries] on an interface or a class.
        var annotated = context.SyntaxProvider.ForAttributeWithMetadataName(
            ModelBuilder.GenerateQueriesAttribute,
            static (node, _) => node is InterfaceDeclarationSyntax or ClassDeclarationSyntax,
            static (attributeContext, cancellationToken) => ModelBuilder.FromInterface(
                (INamedTypeSymbol)attributeContext.TargetSymbol,
                attributeContext.Attributes[0],
                cancellationToken));

        // [assembly: GenerateQueries(typeof(IApi))] for interfaces that cannot be annotated (generated from OpenAPI,
        // defined in another assembly).
        var assemblyLevel = context.CompilationProvider.SelectMany(static (compilation, cancellationToken) =>
            ModelBuilder.FromAssembly(compilation, cancellationToken));

        context.RegisterSourceOutput(annotated, static (output, api) => Generate(output, api));
        context.RegisterSourceOutput(assemblyLevel, static (output, api) => Generate(output, api));
    }

    private static void Generate(SourceProductionContext output, ApiModel api)
    {
        foreach (var diagnostic in api.Diagnostics)
        {
            output.ReportDiagnostic(diagnostic.ToDiagnostic());
        }

        if (api.Methods.Count > 0 || api.Diagnostics.All(d => d.Id != Diagnostics.InvalidTarget.Id))
        {
            output.AddSource(api.HintName, Emitter.Emit(api));
        }
    }
}
