namespace DotNetQuery;

/// <summary>Selects queries by whether anything currently observes them.</summary>
public enum QueryTypeFilter
{
    /// <summary>Every query, observed or not.</summary>
    All,

    /// <summary>Queries with at least one observer (on screen).</summary>
    Active,

    /// <summary>Queries with no observers (cached in the background).</summary>
    Inactive,
}

/// <summary>Which of the invalidated queries <c>InvalidateQueriesAsync(QueryFilter, ...)</c> refetches right away.</summary>
public enum RefetchType
{
    /// <summary>Only mark the queries stale; each refetches the next time it is used.</summary>
    None,

    /// <summary>Refetch the queries that are currently observed (the default, and what key-based invalidation does).</summary>
    Active,

    /// <summary>Refetch only the queries that are not currently observed.</summary>
    Inactive,

    /// <summary>Refetch every matched query that has been fetched before.</summary>
    All,
}

/// <summary>
/// Describes a set of cached queries - by key (prefix or exact), activity, staleness, fetch state, status or
/// an arbitrary predicate - for bulk operations such as invalidating, refetching, cancelling, resetting,
/// removing, counting or inspecting queries. Every criterion left unset matches everything; set criteria are
/// combined with AND.
/// </summary>
/// <example>
/// <code>
/// // Refetch every stale todo query that is on screen.
/// await client.RefetchQueriesAsync(new QueryFilter { Key = TodoQueries.All, Type = QueryTypeFilter.Active, Stale = true });
///
/// // Drop everything cached for a tenant that is no longer selected.
/// client.RemoveQueries(QueryFilter.ForKey(QueryKey.Create("tenant", oldTenantId)));
/// </code>
/// </example>
public sealed record QueryFilter
{
    /// <summary>A filter that matches every query.</summary>
    public static QueryFilter All { get; } = new();

    /// <summary>
    /// Matches queries whose key starts with this key (hierarchical prefix match), or equals it when
    /// <see cref="Exact"/> is set. <see langword="null"/> matches every key.
    /// </summary>
    public QueryKey? Key { get; init; }

    /// <summary>When <see langword="true"/>, <see cref="Key"/> must match exactly instead of as a prefix.</summary>
    public bool Exact { get; init; }

    /// <summary>Matches queries by whether they are observed. Defaults to <see cref="QueryTypeFilter.All"/>.</summary>
    public QueryTypeFilter Type { get; init; }

    /// <summary>When set, matches only stale (<see langword="true"/>) or only fresh (<see langword="false"/>) queries.</summary>
    public bool? Stale { get; init; }

    /// <summary>When set, matches only queries that are (<see langword="true"/>) or are not (<see langword="false"/>) fetching.</summary>
    public bool? Fetching { get; init; }

    /// <summary>When set, matches only queries in this status.</summary>
    public QueryStatus? Status { get; init; }

    /// <summary>
    /// An additional condition evaluated against a point-in-time description of each query that passed the
    /// other criteria. Runs on the calling thread, outside any cache lock.
    /// </summary>
    public Func<QueryDescriptor, bool>? Predicate { get; init; }

    /// <summary>Creates a filter matching <paramref name="key"/> as a prefix, or exactly when <paramref name="exact"/> is set.</summary>
    public static QueryFilter ForKey(QueryKey key, bool exact = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new QueryFilter { Key = key, Exact = exact };
    }

    /// <summary>Returns whether <paramref name="key"/> satisfies the key criterion of this filter.</summary>
    public bool MatchesKey(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Key is not { } filterKey || (Exact ? filterKey == key : filterKey.IsPrefixOf(key));
    }

    /// <summary>Returns whether <paramref name="query"/> satisfies every criterion of this filter.</summary>
    public bool Matches(QueryDescriptor query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return MatchesKey(query.Key)
            && MatchesState(query.IsActive, query.IsStale, query.Status is QueryStatus.Loading or QueryStatus.Refetching, query.Status)
            && (Predicate is null || Predicate(query));
    }

    internal bool MatchesState(bool isActive, bool isStale, bool isFetching, QueryStatus status) =>
        Type switch
        {
            QueryTypeFilter.Active => isActive,
            QueryTypeFilter.Inactive => !isActive,
            _ => true,
        }
        && (Stale is not { } stale || stale == isStale)
        && (Fetching is not { } fetching || fetching == isFetching)
        && (Status is not { } wanted || wanted == status);
}
