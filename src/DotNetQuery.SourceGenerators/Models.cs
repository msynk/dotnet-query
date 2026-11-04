using System.Collections;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetQuery.SourceGenerators;

// The generator's pipeline models. They hold only strings and value types (never symbols or syntax), so the
// incremental pipeline can compare them and skip regenerating code when nothing relevant changed.

internal enum MethodKind
{
    Query,
    Mutation,
    InfiniteQuery,
}

internal enum ReturnKind
{
    TaskOfT,
    ValueTaskOfT,
    Task,
    ValueTask,
}

internal sealed record ParameterModel(string Name, string Type, bool IsCancellationToken, string? DefaultValue);

internal sealed record MethodModel(
    MethodKind Kind,
    string Name,
    string MethodName,
    ReturnKind ReturnKind,
    string DataType,
    EquatableArray<ParameterModel> Parameters,
    EquatableArray<string>? Invalidates,
    string TypeParameters,
    EquatableArray<string> Constraints,
    string? PageParameter)
{
    public IEnumerable<ParameterModel> ValueParameters => Parameters.Where(p => !p.IsCancellationToken);

    // The parameters that identify the query: every value parameter except an infinite query's page parameter.
    public IEnumerable<ParameterModel> KeyParameters => ValueParameters.Where(p => p.Name != PageParameter);

    public ParameterModel? Page => PageParameter is null ? null : Parameters.FirstOrDefault(p => p.Name == PageParameter);

    public bool HasCancellationToken => Parameters.Any(p => p.IsCancellationToken);

    public bool IsKeyed => Kind is MethodKind.Query or MethodKind.InfiniteQuery;

    // " where T : class" clauses (with a leading space), or empty.
    public string ConstraintClauses => Constraints.Count == 0 ? string.Empty : " " + string.Join(" ", Constraints);
}

internal sealed record ApiModel(
    string? Namespace,
    string ClassName,
    string Accessibility,
    string InterfaceType,
    string InterfaceName,
    string KeyPrefix,
    EquatableArray<MethodModel> Methods,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public string HintName => (Namespace is null ? ClassName : Namespace + "." + ClassName) + ".g.cs";
}

internal sealed record DiagnosticInfo(string Id, string Message, string? FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, ISymbol? symbol, params object[] args)
    {
        var location = symbol?.Locations.FirstOrDefault(l => l.IsInSource);
        return new DiagnosticInfo(
            descriptor.Id,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, descriptor.MessageFormat.ToString(System.Globalization.CultureInfo.InvariantCulture), args),
            location?.SourceTree?.FilePath,
            location?.SourceSpan ?? default,
            location?.GetLineSpan().Span ?? default);
    }

    public Diagnostic ToDiagnostic()
    {
        var descriptor = Diagnostics.All.First(d => d.Id == Id);
        var location = FilePath is null ? Location.None : Location.Create(FilePath, Span, LineSpan);
        return Diagnostic.Create(new DiagnosticDescriptor(descriptor.Id, descriptor.Title, Message, descriptor.Category, descriptor.DefaultSeverity, true), location);
    }
}

// An immutable array compared by its elements.
internal readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items = items;

    public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public int Count => Items.Length;

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in Items)
        {
            hash = (hash * 31) + (item?.GetHashCode() ?? 0);
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class Diagnostics
{
    public static readonly DiagnosticDescriptor UnsupportedMethod = new(
        "DNQ001",
        "API method cannot be generated",
        "'{0}' was not generated: {1}",
        "DotNetQuery",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnknownInvalidation = new(
        "DNQ002",
        "Unknown query in Invalidates",
        "Mutation '{0}' invalidates '{1}', which is not a query method of '{2}'",
        "DotNetQuery",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidTarget = new(
        "DNQ003",
        "GenerateQueries target is not an accessible interface or class",
        "'{0}' is not a non-generic interface or non-static class accessible to the generated code; no queries were generated",
        "DotNetQuery",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly ImmutableArray<DiagnosticDescriptor> All = ImmutableArray.Create(UnsupportedMethod, UnknownInvalidation, InvalidTarget);
}
