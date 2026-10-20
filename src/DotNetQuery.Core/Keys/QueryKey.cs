using System.Collections;
using System.Globalization;
using System.Text;

namespace DotNetQuery;

/// <summary>
/// An immutable, hierarchical, hashable identifier for a query.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="QueryKey"/> is an ordered sequence of segments (for example
/// <c>["users", 42]</c>). Keys are compared structurally, cache a stable hash code,
/// and support hierarchical matching via <see cref="IsPrefixOf"/> so that a broad key
/// such as <c>["users"]</c> can invalidate every more specific key beneath it.
/// </para>
/// <para>
/// Segments should be immutable values with value equality: strings, numbers, enums, <see cref="Guid"/>s,
/// dates, records, or tuples. List segments (arrays, <see cref="List{T}"/> and any other
/// <see cref="IList"/>) are snapshotted and compared element by element, so <c>Create("todos", new[] { 1, 2 })</c>
/// equals a second key built from a different array with the same elements.
/// </para>
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

        // Defensive, normalizing copy so callers cannot mutate the key after construction.
        var copy = new object?[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            copy[i] = Normalize(segments[i]);
        }

        return new QueryKey(copy);
    }

    /// <summary>
    /// Creates a query key that begins with the name of <typeparamref name="T"/> (generic arguments
    /// included, for example <c>"List&lt;Todo&gt;"</c>), followed by the supplied segments
    /// (for example <c>Create&lt;User&gt;("profile", id)</c>).
    /// </summary>
    /// <typeparam name="T">The entity type whose name prefixes the key.</typeparam>
    /// <param name="segments">Additional ordered segments appended after the type name.</param>
    public static QueryKey Create<T>(params object?[] segments)
    {
        var withType = new object?[(segments?.Length ?? 0) + 1];
        withType[0] = TypeKeyName<T>.Value;
        for (var i = 0; i < (segments?.Length ?? 0); i++)
        {
            withType[i + 1] = Normalize(segments![i]);
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
        for (var i = 0; i < segments.Length; i++)
        {
            combined[_segments.Length + i] = Normalize(segments[i]);
        }

        return new QueryKey(combined);
    }

    /// <summary>Starts a fluent <see cref="QueryKeyBuilder"/> seeded with the supplied segments.</summary>
    public static QueryKeyBuilder Builder(params object?[] segments) => new(segments);

    /// <summary>
    /// Starts a fluent <see cref="QueryKeyBuilder"/> seeded with the name of <typeparamref name="T"/>,
    /// followed by any supplied segments (mirrors <see cref="Create{T}"/>).
    /// </summary>
    public static QueryKeyBuilder Builder<T>(params object?[] segments)
    {
        var seed = new object?[(segments?.Length ?? 0) + 1];
        seed[0] = TypeKeyName<T>.Value;
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

    /// <summary>
    /// Returns a stable, culture-invariant, human-readable representation such as <c>["users", 42]</c>.
    /// String segments are quoted and escaped, so distinct keys produce distinct strings; the form is
    /// suitable as a persistence or hydration key.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        AppendTo(sb, _segments);
        return sb.ToString();
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

    // Lists compare by reference by default, which would silently split the cache; snapshot them into a
    // structurally-equal segment instead. Strings are IEnumerable but are values, so they pass through.
    private static object? Normalize(object? segment) => segment switch
    {
        null or string or QueryKey or ListSegment => segment,
        IList list => ListSegment.From(list),
        _ => segment,
    };

    private static void AppendTo(StringBuilder sb, IReadOnlyList<object?> segments)
    {
        sb.Append('[');
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            AppendSegment(sb, segments[i]);
        }

        sb.Append(']');
    }

    private static void AppendSegment(StringBuilder sb, object? segment)
    {
        switch (segment)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                sb.Append('"');
                foreach (var c in s)
                {
                    if (c is '"' or '\\')
                    {
                        sb.Append('\\');
                    }

                    sb.Append(c);
                }

                sb.Append('"');
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case ListSegment list:
                AppendTo(sb, list.Items);
                break;
            case QueryKey key:
                AppendTo(sb, key._segments);
                break;
            case DateTime dt:
                sb.Append(dt.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                sb.Append(dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            case IFormattable formattable:
                sb.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append(segment.ToString() ?? "null");
                break;
        }
    }

    /// <summary>An immutable, structurally-equal snapshot of a list segment.</summary>
    private sealed class ListSegment : IEquatable<ListSegment>
    {
        private readonly int _hashCode;

        private ListSegment(object?[] items)
        {
            Items = items;
            _hashCode = ComputeHashCode(items);
        }

        public object?[] Items { get; }

        public static ListSegment From(IList list)
        {
            var items = new object?[list.Count];
            for (var i = 0; i < items.Length; i++)
            {
                items[i] = Normalize(list[i]);
            }

            return new ListSegment(items);
        }

        public bool Equals(ListSegment? other)
        {
            if (other is null || other._hashCode != _hashCode || other.Items.Length != Items.Length)
            {
                return false;
            }

            for (var i = 0; i < Items.Length; i++)
            {
                if (!Equals(Items[i], other.Items[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is ListSegment other && Equals(other);

        public override int GetHashCode() => _hashCode;

        public override string ToString()
        {
            var sb = new StringBuilder();
            AppendTo(sb, Items);
            return sb.ToString();
        }
    }
}

/// <summary>Caches a readable, generic-aware name for <typeparamref name="T"/> (e.g. <c>List&lt;Todo&gt;</c>).</summary>
internal static class TypeKeyName<T>
{
    public static readonly string Value = Format(typeof(T));

    private static string Format(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        var sb = new StringBuilder(tick >= 0 ? name[..tick] : name).Append('<');
        var args = type.GetGenericArguments();
        for (var i = 0; i < args.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Format(args[i]));
        }

        return sb.Append('>').ToString();
    }
}
