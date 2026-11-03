using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotNetQuery.SourceGenerators;

// Turns an API interface (a symbol) into an ApiModel (strings only).
internal static class ModelBuilder
{
    public const string GenerateQueriesAttribute = "DotNetQuery.GenerateQueriesAttribute";
    private const string QueryMethodAttribute = "DotNetQuery.QueryMethodAttribute";
    private const string MutationMethodAttribute = "DotNetQuery.MutationMethodAttribute";
    private const string QueryIgnoreAttribute = "DotNetQuery.QueryIgnoreAttribute";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly string[] QueryPrefixes = ["Get", "List", "Find", "Search", "Fetch", "Load", "Query", "Count", "Read"];

    // Assembly-level [GenerateQueries(typeof(IApi))], for interfaces the user cannot annotate.
    public static ImmutableArray<ApiModel> FromAssembly(Compilation compilation, CancellationToken cancellationToken)
    {
        var builder = ImmutableArray.CreateBuilder<ApiModel>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() != GenerateQueriesAttribute
                || attribute.ConstructorArguments.Length != 1
                || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol api
                || !seen.Add(api)
                || HasOwnAttribute(api))
            {
                continue; // an interface annotated directly is generated from its own attribute
            }

            builder.Add(Build(api, attribute, compilation.IsSymbolAccessibleWithin(api, compilation.Assembly), cancellationToken));
        }

        return builder.ToImmutable();
    }

    public static ApiModel FromInterface(INamedTypeSymbol api, AttributeData attribute, CancellationToken cancellationToken) =>
        Build(api, attribute, accessible: true, cancellationToken);

    private static bool HasOwnAttribute(INamedTypeSymbol api) =>
        api.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == GenerateQueriesAttribute);

    private static ApiModel Build(INamedTypeSymbol api, AttributeData attribute, bool accessible, CancellationToken cancellationToken)
    {
        var baseName = api.Name.Length > 1 && api.Name[0] == 'I' && char.IsUpper(api.Name[1]) ? api.Name.Substring(1) : api.Name;
        var keyPrefix = NamedString(attribute, "KeyPrefix") ?? baseName;
        var className = NamedString(attribute, "ClassName") ?? baseName + "Queries";
        var ns = api.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : null;
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        if (api.TypeKind != TypeKind.Interface || api.IsGenericType || !accessible || !IsAccessible(api))
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.InvalidTarget, api, api.ToDisplayString()));
            return new ApiModel(ns, className, "internal", api.ToDisplayString(TypeFormat), api.Name, keyPrefix, default, new(diagnostics.ToImmutable()));
        }

        // Classify and describe every method (including inherited interface members).
        var candidates = new List<(IMethodSymbol Symbol, MethodModel Model, string[]? Invalidates)>();
        foreach (var method in api.GetMembers().Concat(api.AllInterfaces.SelectMany(i => i.GetMembers())).OfType<IMethodSymbol>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method.MethodKind != Microsoft.CodeAnalysis.MethodKind.Ordinary || method.IsStatic || HasAttribute(method, QueryIgnoreAttribute))
            {
                continue;
            }

            var explicitQuery = FindAttribute(method, QueryMethodAttribute);
            var explicitMutation = FindAttribute(method, MutationMethodAttribute);
            var kind = explicitQuery is not null ? MethodKind.Query
                : explicitMutation is not null ? MethodKind.Mutation
                : RefitKind(method) ?? (QueryPrefixes.Any(p => StartsWithWord(method.Name, p)) ? MethodKind.Query : MethodKind.Mutation);
            var isExplicit = explicitQuery is not null || explicitMutation is not null;

            var problem = Describe(method, kind, out var returnKind, out var dataType, out var parameters);
            if (problem is not null)
            {
                if (isExplicit)
                {
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnsupportedMethod, method, method.Name, problem));
                }

                continue; // unannotated helpers that do not fit (properties, sync methods) are skipped silently
            }

            var name = NamedString(explicitQuery ?? explicitMutation, "Name") ?? StripAsync(method.Name);
            var model = new MethodModel(kind, name, method.Name, returnKind, dataType!, new(parameters), null);
            candidates.Add((method, model, explicitMutation is null ? null : NamedStrings(explicitMutation, "Invalidates")));
        }

        // Overloads that differ only by a CancellationToken (NSwag emits both) generate one member: keep the one with
        // the token.
        var methods = new List<(IMethodSymbol Symbol, MethodModel Model, string[]? Invalidates)>();
        foreach (var candidate in candidates)
        {
            var index = methods.FindIndex(m => m.Model.Kind == candidate.Model.Kind && Signature(m.Model) == Signature(candidate.Model));
            if (index < 0)
            {
                methods.Add(candidate);
            }
            else if (!methods[index].Model.HasCancellationToken && candidate.Model.HasCancellationToken)
            {
                methods[index] = candidate;
            }
        }

        // Resolve [MutationMethod(Invalidates = ...)] against the query methods.
        var queries = methods.Where(m => m.Model.Kind == MethodKind.Query).ToList();
        var resolved = ImmutableArray.CreateBuilder<MethodModel>();
        foreach (var (symbol, model, invalidates) in methods)
        {
            if (invalidates is null)
            {
                resolved.Add(model);
                continue;
            }

            var names = new List<string>();
            foreach (var target in invalidates)
            {
                var query = queries.FirstOrDefault(q => q.Model.MethodName == target || q.Model.Name == target);
                if (query.Model is null)
                {
                    diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnknownInvalidation, symbol, model.Name, target, api.Name));
                }
                else if (!names.Contains(query.Model.Name))
                {
                    names.Add(query.Model.Name);
                }
            }

            resolved.Add(model with { Invalidates = new EquatableArray<string>(names.ToImmutableArray()) });
        }

        return new ApiModel(
            ns,
            className,
            IsPublic(api) ? "public" : "internal",
            api.ToDisplayString(TypeFormat),
            api.Name,
            keyPrefix,
            new(resolved.ToImmutable()),
            new(diagnostics.ToImmutable()));
    }

    private static string? Describe(
        IMethodSymbol method,
        MethodKind kind,
        out ReturnKind returnKind,
        out string? dataType,
        out ImmutableArray<ParameterModel> parameters)
    {
        returnKind = default;
        dataType = null;
        parameters = default;

        if (method.IsGenericMethod)
        {
            return "generic methods are not supported";
        }

        if (method.ReturnType is not INamedTypeSymbol returnType || !TryReturnKind(returnType, out returnKind, out dataType))
        {
            return "it must return Task<T> or ValueTask<T> (mutations may also return Task or ValueTask)";
        }

        if (kind == MethodKind.Query && returnKind is ReturnKind.Task or ReturnKind.ValueTask)
        {
            return "a query must return data (Task<T> or ValueTask<T>)";
        }

        var builder = ImmutableArray.CreateBuilder<ParameterModel>();
        var tokens = 0;
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
            {
                return "ref, out and in parameters are not supported";
            }

            var isToken = parameter.Type.ToDisplayString() == "System.Threading.CancellationToken";
            tokens += isToken ? 1 : 0;
            builder.Add(new ParameterModel(
                Identifier(parameter.Name),
                parameter.Type.ToDisplayString(TypeFormat),
                isToken,
                parameter.HasExplicitDefaultValue ? DefaultValue(parameter) : null));
        }

        if (tokens > 1)
        {
            return "only one CancellationToken parameter is supported";
        }

        parameters = builder.ToImmutable();
        return null;
    }

    private static bool TryReturnKind(INamedTypeSymbol type, out ReturnKind kind, out string? dataType)
    {
        var definition = type.OriginalDefinition.ToDisplayString();
        (kind, dataType) = definition switch
        {
            "System.Threading.Tasks.Task<TResult>" => (ReturnKind.TaskOfT, type.TypeArguments[0].ToDisplayString(TypeFormat)),
            "System.Threading.Tasks.ValueTask<TResult>" => (ReturnKind.ValueTaskOfT, type.TypeArguments[0].ToDisplayString(TypeFormat)),
            "System.Threading.Tasks.Task" => (ReturnKind.Task, "bool"),
            "System.Threading.Tasks.ValueTask" => (ReturnKind.ValueTask, "bool"),
            _ => (default(ReturnKind), null),
        };
        return dataType is not null;
    }

    private static MethodKind? RefitKind(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass is not { ContainingNamespace: { } ns } attributeClass || ns.ToDisplayString() != "Refit")
            {
                continue;
            }

            switch (attributeClass.Name)
            {
                case "GetAttribute" or "HeadAttribute":
                    return MethodKind.Query;
                case "PostAttribute" or "PutAttribute" or "PatchAttribute" or "DeleteAttribute":
                    return MethodKind.Mutation;
            }
        }

        return null;
    }

    private static string DefaultValue(IParameterSymbol parameter)
    {
        var value = parameter.ExplicitDefaultValue;
        if (value is null)
        {
            return "default";
        }

        var literal = SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false);
        if (parameter.Type.TypeKind == TypeKind.Enum)
        {
            var member = parameter.Type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
            return member is not null
                ? $"{parameter.Type.ToDisplayString(TypeFormat)}.{member.Name}"
                : $"({parameter.Type.ToDisplayString(TypeFormat)}){literal}";
        }

        return value switch
            {
                float => literal + "F",
                long => literal + "L",
                ulong => literal + "UL",
                uint => literal + "U",
                decimal => literal + "M",
                _ => literal,
            };
    }

    private static string Signature(MethodModel model) =>
        model.Name + "(" + string.Join(",", model.ValueParameters.Select(p => p.Type)) + ")";

    private static bool StartsWithWord(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) && (name.Length == prefix.Length || !char.IsLower(name[prefix.Length]));

    private static string StripAsync(string name) =>
        name.Length > 5 && name.EndsWith("Async", StringComparison.Ordinal) ? name.Substring(0, name.Length - 5) : name;

    private static string Identifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static AttributeData? FindAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == name);

    private static bool HasAttribute(ISymbol symbol, string name) => FindAttribute(symbol, name) is not null;

    private static string? NamedString(AttributeData? attribute, string name) =>
        attribute?.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value as string;

    private static string[]? NamedStrings(AttributeData attribute, string name)
    {
        var argument = attribute.NamedArguments.FirstOrDefault(a => a.Key == name);
        return argument.Key is null || argument.Value.IsNull
            ? null
            : argument.Value.Values.Select(v => v.Value as string).Where(v => v is not null).Select(v => v!).ToArray();
    }

    private static bool IsAccessible(INamedTypeSymbol type)
    {
        for (ISymbol? current = type; current is INamedTypeSymbol named; current = named.ContainingType)
        {
            if (named.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPublic(INamedTypeSymbol type)
    {
        for (ISymbol? current = type; current is INamedTypeSymbol named; current = named.ContainingType)
        {
            if (named.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }
}
