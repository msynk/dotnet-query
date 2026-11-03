using System.Collections;

namespace DotNetQuery;

/// <summary>
/// Structural equality and sharing for cached data, without reflection (trim/AOT-safe). Values are equal when
/// they are the same instance, when <see cref="object.Equals(object?)"/> says so (records, strings, primitives,
/// anything implementing value equality), or - for lists and dictionaries - when they have the same shape and
/// every element is structurally equal.
/// </summary>
internal static class StructuralSharing
{
    // Deeper graphs are compared by Equals only; guards against cycles and pathological nesting.
    private const int MaxDepth = 32;

    /// <summary>
    /// Returns <paramref name="previous"/> when <paramref name="next"/> is structurally equal to it; otherwise
    /// returns <paramref name="next"/>, after replacing (in place, when the collection is writable) each list
    /// element or dictionary value that equals its previous counterpart with that previous instance. Never
    /// throws: a failing <c>Equals</c> simply disables sharing for that value.
    /// </summary>
    public static T Share<T>(T? previous, T next)
    {
        if (typeof(T).IsValueType || previous is null || next is null)
        {
            return next;
        }

        try
        {
            return (T)Share(previous, (object)next, 0);
        }
        catch (Exception)
        {
            return next;
        }
    }

    /// <summary>Returns whether <paramref name="x"/> and <paramref name="y"/> are structurally equal. Never throws.</summary>
    public static bool AreEqual(object? x, object? y)
    {
        try
        {
            return DeepEquals(x, y, 0);
        }
        catch (Exception)
        {
            return ReferenceEquals(x, y);
        }
    }

    private static object Share(object previous, object next, int depth)
    {
        if (ReferenceEquals(previous, next))
        {
            return previous;
        }

        if (depth > MaxDepth || previous.GetType() != next.GetType())
        {
            return next;
        }

        if (next is string)
        {
            return next.Equals(previous) ? previous : next;
        }

        if (next is IList nextList && previous is IList previousList)
        {
            var allEqual = nextList.Count == previousList.Count;
            var writable = !nextList.IsReadOnly;
            var count = Math.Min(nextList.Count, previousList.Count);
            for (var i = 0; i < count; i++)
            {
                var before = previousList[i];
                var after = nextList[i];
                var shared = before is null || after is null ? (Equals(before, after) ? before : after) : Share(before, after, depth + 1);
                if (ReferenceEquals(shared, before))
                {
                    // Boxed value-type elements have no identity worth preserving; skip the write.
                    if (writable && !ReferenceEquals(after, before) && after is not ValueType)
                    {
                        nextList[i] = before;
                    }
                }
                else
                {
                    allEqual = false;
                }
            }

            return allEqual ? previous : next;
        }

        if (next is IDictionary nextMap && previous is IDictionary previousMap)
        {
            var allEqual = nextMap.Count == previousMap.Count;
            var reuse = new List<(object Key, object? Value)>();
            foreach (DictionaryEntry entry in nextMap)
            {
                if (!previousMap.Contains(entry.Key))
                {
                    allEqual = false;
                    continue;
                }

                var before = previousMap[entry.Key];
                var after = entry.Value;
                var shared = before is null || after is null ? (Equals(before, after) ? before : after) : Share(before, after, depth + 1);
                if (ReferenceEquals(shared, before))
                {
                    if (!ReferenceEquals(after, before) && after is not ValueType)
                    {
                        reuse.Add((entry.Key, before));
                    }
                }
                else
                {
                    allEqual = false;
                }
            }

            if (allEqual)
            {
                return previous;
            }

            if (!nextMap.IsReadOnly)
            {
                foreach (var (key, value) in reuse)
                {
                    nextMap[key] = value;
                }
            }

            return next;
        }

        return next.Equals(previous) ? previous : next;
    }

    private static bool DeepEquals(object? x, object? y, int depth)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null || x.GetType() != y.GetType())
        {
            return false;
        }

        if (depth <= MaxDepth && x is not string)
        {
            if (x is IList xs && y is IList ys)
            {
                if (xs.Count != ys.Count)
                {
                    return false;
                }

                for (var i = 0; i < xs.Count; i++)
                {
                    if (!DeepEquals(xs[i], ys[i], depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (x is IDictionary xm && y is IDictionary ym)
            {
                if (xm.Count != ym.Count)
                {
                    return false;
                }

                foreach (DictionaryEntry entry in xm)
                {
                    if (!ym.Contains(entry.Key) || !DeepEquals(entry.Value, ym[entry.Key], depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return x.Equals(y);
    }
}

/// <summary>An <see cref="IEqualityComparer{T}"/> over <see cref="StructuralSharing.AreEqual"/>.</summary>
internal sealed class StructuralEqualityComparer<T> : IEqualityComparer<T>
{
    public static StructuralEqualityComparer<T> Instance { get; } = new();

    public bool Equals(T? x, T? y) => typeof(T).IsValueType ? EqualityComparer<T>.Default.Equals(x, y) : StructuralSharing.AreEqual(x, y);

    public int GetHashCode(T obj) => obj is null ? 0 : EqualityComparer<T>.Default.GetHashCode(obj);
}
