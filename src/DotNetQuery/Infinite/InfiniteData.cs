namespace DotNetQuery;

/// <summary>
/// The accumulated result of an infinite query: the fetched pages together with the page
/// parameters that produced them, in order.
/// </summary>
/// <typeparam name="TParam">The type of the page parameter (offset, cursor, etc.).</typeparam>
/// <typeparam name="TPage">The type of a single page of data.</typeparam>
public sealed record InfiniteData<TParam, TPage>
{
    /// <summary>Gets the fetched pages, ordered from first (previous) to last (next).</summary>
    public required IReadOnlyList<TPage> Pages { get; init; }

    /// <summary>Gets the page parameters that produced <see cref="Pages"/>, in the same order.</summary>
    public required IReadOnlyList<TParam> PageParams { get; init; }

    /// <summary>An empty infinite-data value.</summary>
    public static InfiniteData<TParam, TPage> Empty { get; } = new() { Pages = [], PageParams = [] };
}

/// <summary>
/// The result of a <c>GetNextPageParam</c>/<c>GetPreviousPageParam</c> callback: either the parameter
/// for the adjacent page, or <see cref="NextPageParam{T}.None"/> when there is no such page.
/// </summary>
/// <typeparam name="T">The page-parameter type.</typeparam>
public readonly record struct NextPageParam<T>(bool HasValue, T Value)
{
    /// <summary>Indicates there is no further page in this direction.</summary>
    public static NextPageParam<T> None => default;
}

/// <summary>Factory helpers for <see cref="NextPageParam{T}"/> with type inference.</summary>
public static class NextPageParam
{
    /// <summary>Creates a value indicating the parameter for the adjacent page.</summary>
    public static NextPageParam<T> Of<T>(T value) => new(true, value);

    /// <summary>Creates a value indicating there is no further page in this direction.</summary>
    public static NextPageParam<T> None<T>() => default;
}
