using System.Collections;
using System.Text;

namespace DotNetQuery;

/// <summary>
/// An immutable, hierarchical, hashable identifier for a query.
/// </summary>
/// <remarks>
/// A <see cref="QueryKey"/> is an ordered sequence of segments (for example
/// <c>["users", 42]</c>). Keys are compared structurally, cache a stable hash code,
/// and support hierarchical matching via <see cref="IsPrefixOf"/> so that a broad key
/// such as <c>["users"]</c> can invalidate every more specific key beneath it.
/// </remarks>
public sealed class QueryKey : IEquatable<QueryKey>, IReadOnlyList<object?>
{
    private readonly object?[] _segments;
    private readonly int _hashCode;

    private QueryKey(object?[] segments)
    {
        _segments = segments;
        _hashCode = ComputeHashCode(segments);
    }

    /// <summary>Gets the number of segments in the key.</summary>
    public int Count => _segments.Length;

    /// <summary>Gets the segment at the specified index.</summary>
    public object? this[int index] => _segments[index];

    /// <summary>Creates a query key from the supplied ordered segments.</summary>
    /// <param name="segments">The ordered segments that make up the key. Must not be empty.</param>
    /// <exception cref="ArgumentException">The segment array is <see langword="null"/> or empty.</exception>
    public static QueryKey Create(params object?[] segments)
    {
        if (segments is null || segments.Length == 0)
        {
            throw new ArgumentException("A query key must contain at least one segment.", nameof(segments));
        }

        // Defensive copy so callers cannot mutate the key after construction.
        var copy = new object?[segments.Length];
        Array.Copy(segments, copy, segments.Length);
        return new QueryKey(copy);
    }

    /// <summary>
    /// Creates a query key that begins with the runtime type name of <typeparamref name="T"/>,
    /// followed by the supplied segments (for example <c>Create&lt;User&gt;("profile", id)</c>).
    /// </summary>
    /// <typeparam name="T">The entity type whose name prefixes the key.</typeparam>
    /// <param name="segments">Additional ordered segments appended after the type name.</param>
    public static QueryKey Create<T>(params object?[] segments)
    {
        var withType = new object?[(segments?.Length ?? 0) + 1];
        withType[0] = typeof(T).Name;
        if (segments is { Length: > 0 })
        {
            Array.Copy(segments, 0, withType, 1, segments.Length);
        }

        return new QueryKey(withType);
    }

    /// <summary>
    /// Creates a new key by appending <paramref name="segments"/> after this key's segments, deriving a
    /// child key from a parent (for example <c>users.Append(42)</c> → <c>["users", 42]</c>). This key is
    /// always a <see cref="IsPrefixOf"/> the result, so invalidating the parent invalidates the child.
    /// Returns this key unchanged when no segments are supplied.
    /// </summary>
    public QueryKey Append(params object?[] segments)
    {
        if (segments is null || segments.Length == 0)
        {
            return this;
        }

        var combined = new object?[_segments.Length + segments.Length];
        Array.Copy(_segments, combined, _segments.Length);
        Array.Copy(segments, 0, combined, _segments.Length, segments.Length);
        return new QueryKey(combined);
    }

    /// <summary>Starts a fluent <see cref="QueryKeyBuilder"/> seeded with the supplied segments.</summary>
    public static QueryKeyBuilder Builder(params object?[] segments) => new(segments);

    /// <summary>
    /// Starts a fluent <see cref="QueryKeyBuilder"/> seeded with the runtime type name of
    /// <typeparamref name="T"/>, followed by any supplied segments (mirrors <see cref="Create{T}"/>).
    /// </summary>
    public static QueryKeyBuilder Builder<T>(params object?[] segments)
    {
        var seed = new object?[(segments?.Length ?? 0) + 1];
        seed[0] = typeof(T).Name;
        if (segments is { Length: > 0 })
        {
            Array.Copy(segments, 0, seed, 1, segments.Length);
        }

        return new QueryKeyBuilder(seed);
    }

    /// <summary>
    /// Determines whether this key is a hierarchical prefix of <paramref name="other"/>.
    /// The key <c>["users"]</c> is a prefix of <c>["users", 42]</c>, enabling partial invalidation.
    /// </summary>
    public bool IsPrefixOf(QueryKey other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_segments.Length > other._segments.Length)
        {
            return false;
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            if (!Equals(_segments[i], other._segments[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool Equals(QueryKey? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_hashCode != other._hashCode || _segments.Length != other._segments.Length)
        {
            return false;
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            if (!Equals(_segments[i], other._segments[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is QueryKey key && Equals(key);

    /// <inheritdoc />
    public override int GetHashCode() => _hashCode;

    /// <summary>Returns a stable, human-readable representation such as <c>["users", 42]</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < _segments.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            var segment = _segments[i];
            if (segment is string s)
            {
                sb.Append('"').Append(s).Append('"');
            }
            else
            {
                sb.Append(segment?.ToString() ?? "null");
            }
        }

        return sb.Append(']').ToString();
    }

    /// <inheritdoc />
    public IEnumerator<object?> GetEnumerator() => ((IEnumerable<object?>)_segments).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _segments.GetEnumerator();

    /// <summary>Structural equality operator.</summary>
    public static bool operator ==(QueryKey? left, QueryKey? right) => Equals(left, right);

    /// <summary>Structural inequality operator.</summary>
    public static bool operator !=(QueryKey? left, QueryKey? right) => !Equals(left, right);

    private static int ComputeHashCode(object?[] segments)
    {
        var hash = new HashCode();
        foreach (var segment in segments)
        {
            hash.Add(segment);
        }

        return hash.ToHashCode();
    }
}
