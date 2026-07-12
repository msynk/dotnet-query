namespace DotNetQuery;

/// <summary>
/// A small fluent builder for constructing a <see cref="QueryKey"/> segment by segment. Useful for
/// defining keys in one place — for example a typed key factory — so segment names are not repeated
/// across call sites. Start one with <see cref="QueryKey.Builder(object?[])"/>.
/// </summary>
/// <remarks>
/// The builder is mutable and not thread-safe; build one key per instance. <see cref="Build"/> may be
/// called more than once and returns an equivalent key each time.
/// </remarks>
public sealed class QueryKeyBuilder
{
    private readonly List<object?> _segments;

    internal QueryKeyBuilder(object?[]? seed) =>
        _segments = seed is { Length: > 0 } ? [.. seed] : [];

    /// <summary>Gets the number of segments accumulated so far.</summary>
    public int Count => _segments.Count;

    /// <summary>Appends a single segment and returns the same builder for chaining.</summary>
    public QueryKeyBuilder Add(object? segment)
    {
        _segments.Add(segment);
        return this;
    }

    /// <summary>Appends several segments in order and returns the same builder for chaining.</summary>
    public QueryKeyBuilder AddRange(params object?[] segments)
    {
        if (segments is { Length: > 0 })
        {
            _segments.AddRange(segments);
        }

        return this;
    }

    /// <summary>Builds an immutable <see cref="QueryKey"/> from the accumulated segments.</summary>
    /// <exception cref="InvalidOperationException">No segments have been added.</exception>
    public QueryKey Build()
    {
        if (_segments.Count == 0)
        {
            throw new InvalidOperationException("A query key must contain at least one segment.");
        }

        return QueryKey.Create([.. _segments]);
    }
}
