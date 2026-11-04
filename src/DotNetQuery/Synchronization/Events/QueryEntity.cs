using System.Collections.Immutable;

namespace DotNetQuery.Synchronization;

/// <summary>What <see cref="QueryEventMapping{TEvent}.Upsert"/> does with a cached collection that does not contain the entity.</summary>
public enum EntityMissingBehavior
{
    /// <summary>
    /// Invalidate the collection: its membership, order or totals cannot be known from the event (a filtered, sorted or
    /// paginated list), so it is refetched. The default, and always correct.
    /// </summary>
    Invalidate,

    /// <summary>Append the entity (to the last page of an infinite query).</summary>
    Append,

    /// <summary>Insert the entity first (into the first page of an infinite query).</summary>
    Prepend,

    /// <summary>Leave the collection unchanged.</summary>
    Ignore,
}

/// <summary>What <see cref="QueryEventMapping{TEvent}.Remove"/> does with a cached detail query of the removed entity.</summary>
public enum EntityDetailRemoval
{
    /// <summary>Invalidate it: observed details refetch (and typically surface a not-found error). The default.</summary>
    Invalidate,

    /// <summary>Remove it from the cache (an observed query is reset and refetched, see <see cref="IQueryClient.RemoveQuery"/>).</summary>
    Remove,

    /// <summary>Leave it unchanged.</summary>
    Ignore,
}

/// <summary>Factory for <see cref="QueryEntity{TItem, TId}"/>.</summary>
public static class QueryEntity
{
    /// <summary>
    /// Describes an entity type cached under <paramref name="key"/>: how to identify an item, optionally how to order its
    /// versions, and (with <see cref="QueryEntity{TItem, TId}.WithCollection"/> and
    /// <see cref="QueryEntity{TItem, TId}.WithInfinitePages"/>) which cached shapes hold it. Detail queries
    /// (<typeparamref name="TItem"/>) and lists (<see cref="List{T}"/>, <see cref="IReadOnlyList{T}"/>, arrays,
    /// <see cref="ImmutableArray{T}"/> and <see cref="ImmutableList{T}"/> of <typeparamref name="TItem"/>) are recognized
    /// without configuration.
    /// </summary>
    /// <param name="key">The key prefix under which every query holding the entity lives.</param>
    /// <param name="id">Returns an item's identity.</param>
    /// <param name="version">
    /// Returns an item's version (for example a row version or an update counter). When set, a cached item is replaced
    /// only by an item with a greater version, so an older event or a late mutation response cannot overwrite newer
    /// data - whoever wrote it.
    /// </param>
    /// <param name="detailKey">
    /// Returns the key of an item's detail query. When set, detail queries are found by key instead of by the id of the
    /// data they hold, which also covers a detail whose first fetch is still in flight.
    /// </param>
    /// <param name="comparer">Compares identities. Defaults to <see cref="EqualityComparer{T}.Default"/>.</param>
    public static QueryEntity<TItem, TId> Create<TItem, TId>(
        QueryKey key,
        Func<TItem, TId> id,
        Func<TItem, long>? version = null,
        Func<TId, QueryKey>? detailKey = null,
        IEqualityComparer<TId>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(id);
        return new QueryEntity<TItem, TId>(key, id, version, detailKey, comparer ?? EqualityComparer<TId>.Default, DefaultShapes<TItem, TId>());
    }

    private static ImmutableArray<EntityShape<TItem, TId>> DefaultShapes<TItem, TId>() =>
    [
        new ListShape<TItem, TId, List<TItem>>(static list => list, static (_, items) => items as List<TItem> ?? [.. items]),
        new ListShape<TItem, TId, IReadOnlyList<TItem>>(static list => list, static (_, items) => items),
        new ListShape<TItem, TId, TItem[]>(static array => array, static (_, items) => [.. items]),
        new ListShape<TItem, TId, ImmutableArray<TItem>>(static array => array, static (_, items) => [.. items]),
        new ListShape<TItem, TId, ImmutableList<TItem>>(static list => list, static (_, items) => [.. items]),
    ];
}

/// <summary>
/// Describes an entity type for event-driven cache updates (<see cref="QueryEventMapping{TEvent}.Upsert"/> and
/// <see cref="QueryEventMapping{TEvent}.Remove"/>): the key prefix its queries live under, its identity, its optional
/// version, and the cached shapes that hold it. Create it once with <see cref="QueryEntity.Create"/>; instances are
/// immutable and thread-safe.
/// </summary>
/// <typeparam name="TItem">The entity type.</typeparam>
/// <typeparam name="TId">The identity type.</typeparam>
public sealed class QueryEntity<TItem, TId>
{
    internal QueryEntity(
        QueryKey key,
        Func<TItem, TId> id,
        Func<TItem, long>? version,
        Func<TId, QueryKey>? detailKey,
        IEqualityComparer<TId> comparer,
        ImmutableArray<EntityShape<TItem, TId>> shapes)
    {
        Key = key;
        IdOf = id;
        VersionOf = version;
        DetailKey = detailKey;
        Comparer = comparer;
        Shapes = shapes;
    }

    /// <summary>Gets the key prefix under which every query holding the entity lives.</summary>
    public QueryKey Key { get; }

    internal Func<TItem, TId> IdOf { get; }

    internal Func<TItem, long>? VersionOf { get; }

    internal Func<TId, QueryKey>? DetailKey { get; }

    internal IEqualityComparer<TId> Comparer { get; }

    internal ImmutableArray<EntityShape<TItem, TId>> Shapes { get; }

    /// <summary>
    /// Returns a copy that also recognizes cached data of type <typeparamref name="TData"/> holding a collection of the
    /// entity - for example a page result <c>Paged&lt;Todo&gt;(Items, Total)</c>.
    /// </summary>
    /// <param name="items">Reads the items.</param>
    /// <param name="withItems">Returns a copy of the data with other items (do not mutate the original).</param>
    /// <param name="refetchOnMembershipChange">
    /// Whether an insertion or removal also invalidates the query, because the data is a window of a larger collection
    /// (offset pagination, totals) that the event alone cannot keep correct. Replacing an item in place never does.
    /// </param>
    public QueryEntity<TItem, TId> WithCollection<TData>(
        Func<TData, IReadOnlyList<TItem>> items,
        Func<TData, IReadOnlyList<TItem>, TData> withItems,
        bool refetchOnMembershipChange = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(withItems);
        return With(new ListShape<TItem, TId, TData>(items, withItems, refetchOnMembershipChange));
    }

    /// <summary>
    /// Returns a copy that also recognizes infinite queries whose pages (<typeparamref name="TPage"/>) hold the entity.
    /// </summary>
    /// <param name="items">Reads a page's items.</param>
    /// <param name="withItems">Returns a copy of a page with other items.</param>
    /// <param name="refetchOnMembershipChange">
    /// Whether an insertion or removal also invalidates the query (refetching its pages), because page boundaries move
    /// with offset pagination. Defaults to <see langword="true"/>; pass <see langword="false"/> for cursor pagination,
    /// where local insertions and removals keep the cursors valid.
    /// </param>
    public QueryEntity<TItem, TId> WithInfinitePages<TParam, TPage>(
        Func<TPage, IReadOnlyList<TItem>> items,
        Func<TPage, IReadOnlyList<TItem>, TPage> withItems,
        bool refetchOnMembershipChange = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(withItems);
        return With(new InfiniteShape<TItem, TId, TParam, TPage>(items, withItems, refetchOnMembershipChange));
    }

    private QueryEntity<TItem, TId> With(EntityShape<TItem, TId> shape) =>
        new(Key, IdOf, VersionOf, DetailKey, Comparer, Shapes.Add(shape));

    internal bool SameId(TItem item, TId id) => Comparer.Equals(IdOf(item), id);

    /// <summary>Whether <paramref name="incoming"/> may replace <paramref name="cached"/> (it is newer, or versions are not tracked).</summary>
    internal bool IsNewer(TItem incoming, TItem cached) => VersionOf is not { } version || version(incoming) > version(cached);
}

/// <summary>An upsert or removal of one entity, applied to every cached shape that holds it.</summary>
internal abstract class EntityOperation<TItem, TId>(QueryEntity<TItem, TId> entity)
{
    public QueryEntity<TItem, TId> Entity { get; } = entity;

    public abstract TId Id { get; }

    /// <summary>Applies the operation to a list. Returns <see langword="null"/> when nothing changed.</summary>
    public abstract List<TItem>? ApplyToList(IReadOnlyList<TItem> items, EntityWrite write, bool refetchOnMembershipChange);

    /// <summary>Applies the operation to a detail value holding the same entity. Returns <see langword="false"/> when unchanged.</summary>
    public abstract bool ApplyToDetail(TItem cached, EntityWrite write, out TItem result);

    /// <summary>
    /// Applies the operation to the pages of an infinite query. Returns <see langword="null"/> when nothing changed.
    /// </summary>
    public List<TPage>? ApplyToPages<TPage>(
        IReadOnlyList<TPage> pages,
        Func<TPage, IReadOnlyList<TItem>> items,
        Func<TPage, IReadOnlyList<TItem>, TPage> withItems,
        EntityWrite write,
        bool refetchOnMembershipChange)
    {
        for (var p = 0; p < pages.Count; p++)
        {
            var pageItems = items(pages[p]);
            if (IndexOf(pageItems) < 0)
            {
                continue;
            }

            var changed = ApplyToList(pageItems, write, refetchOnMembershipChange);
            if (changed is null)
            {
                return null;
            }

            var result = new List<TPage>(pages);
            result[p] = withItems(pages[p], changed);
            return result;
        }

        return InsertIntoPages(pages, items, withItems, write, refetchOnMembershipChange);
    }

    /// <summary>Handles an entity found in no page (an upsert may insert it). Returns <see langword="null"/> when unchanged.</summary>
    protected virtual List<TPage>? InsertIntoPages<TPage>(
        IReadOnlyList<TPage> pages,
        Func<TPage, IReadOnlyList<TItem>> items,
        Func<TPage, IReadOnlyList<TItem>, TPage> withItems,
        EntityWrite write,
        bool refetchOnMembershipChange) => null;

    public int IndexOf(IReadOnlyList<TItem> items)
    {
        var id = Id;
        for (var i = 0; i < items.Count; i++)
        {
            if (Entity.SameId(items[i], id))
            {
                return i;
            }
        }

        return -1;
    }
}

internal sealed class UpsertOperation<TItem, TId>(QueryEntity<TItem, TId> entity, TItem item, EntityMissingBehavior whenMissing)
    : EntityOperation<TItem, TId>(entity)
{
    public override TId Id { get; } = entity.IdOf(item);

    public override List<TItem>? ApplyToList(IReadOnlyList<TItem> items, EntityWrite write, bool refetchOnMembershipChange)
    {
        var index = IndexOf(items);
        if (index >= 0)
        {
            if (!Entity.IsNewer(item, items[index]))
            {
                write.Stale = true;
                return null;
            }

            var replaced = new List<TItem>(items);
            replaced[index] = item;
            return replaced;
        }

        switch (whenMissing)
        {
            case EntityMissingBehavior.Append:
            case EntityMissingBehavior.Prepend:
                write.NeedsInvalidate |= refetchOnMembershipChange;
                var inserted = new List<TItem>(items.Count + 1);
                if (whenMissing is EntityMissingBehavior.Prepend)
                {
                    inserted.Add(item);
                    inserted.AddRange(items);
                }
                else
                {
                    inserted.AddRange(items);
                    inserted.Add(item);
                }

                return inserted;

            case EntityMissingBehavior.Invalidate:
                write.NeedsInvalidate = true;
                return null;

            default:
                return null;
        }
    }

    public override bool ApplyToDetail(TItem cached, EntityWrite write, out TItem result)
    {
        result = item;
        if (!Entity.IsNewer(item, cached))
        {
            write.Stale = true;
            return false;
        }

        return true;
    }

    protected override List<TPage>? InsertIntoPages<TPage>(
        IReadOnlyList<TPage> pages,
        Func<TPage, IReadOnlyList<TItem>> items,
        Func<TPage, IReadOnlyList<TItem>, TPage> withItems,
        EntityWrite write,
        bool refetchOnMembershipChange)
    {
        if (whenMissing is EntityMissingBehavior.Invalidate || (pages.Count == 0 && whenMissing is not EntityMissingBehavior.Ignore))
        {
            write.NeedsInvalidate = true;
            return null;
        }

        if (whenMissing is EntityMissingBehavior.Ignore)
        {
            return null;
        }

        var target = whenMissing is EntityMissingBehavior.Prepend ? 0 : pages.Count - 1;
        var changed = ApplyToList(items(pages[target]), write, refetchOnMembershipChange);
        if (changed is null)
        {
            return null;
        }

        var result = new List<TPage>(pages);
        result[target] = withItems(pages[target], changed);
        return result;
    }
}

internal sealed class RemoveOperation<TItem, TId>(QueryEntity<TItem, TId> entity, TId id, EntityDetailRemoval detail)
    : EntityOperation<TItem, TId>(entity)
{
    public override TId Id { get; } = id;

    public override List<TItem>? ApplyToList(IReadOnlyList<TItem> items, EntityWrite write, bool refetchOnMembershipChange)
    {
        var index = IndexOf(items);
        if (index < 0)
        {
            return null;
        }

        write.NeedsInvalidate |= refetchOnMembershipChange;
        var remaining = new List<TItem>(items);
        remaining.RemoveAt(index);
        return remaining;
    }

    public override bool ApplyToDetail(TItem cached, EntityWrite write, out TItem result)
    {
        result = cached;
        switch (detail)
        {
            case EntityDetailRemoval.Invalidate:
                write.NeedsInvalidate = true;
                break;
            case EntityDetailRemoval.Remove:
                write.NeedsRemoval = true;
                break;
        }

        return false;
    }
}

/// <summary>The outcome of an entity operation on one cache entry, filled in by the updater (its last run wins).</summary>
internal sealed class EntityWrite(IQueryEntry entry)
{
    public IQueryEntry Entry { get; } = entry;

    /// <summary>Whether the entry holds (or may hold) the entity, so a fetch in flight that predates the event must be superseded.</summary>
    public bool Relevant { get; set; } = true;

    public bool NeedsInvalidate { get; set; }

    public bool NeedsRemoval { get; set; }

    public bool Stale { get; set; }

    public void Reset()
    {
        NeedsInvalidate = false;
        NeedsRemoval = false;
        Stale = false;
    }
}

/// <summary>A cached shape holding entities: binds matching cache entries into a transaction.</summary>
internal abstract class EntityShape<TItem, TId>
{
    /// <summary>Adds the operation's write for <paramref name="entry"/> to <paramref name="batch"/> if the entry has this shape.</summary>
    public abstract bool TryAdd(IQueryEntry entry, EntityOperation<TItem, TId> operation, QueryDataBatch batch, List<EntityWrite> writes);
}

internal sealed class ListShape<TItem, TId, TData>(
    Func<TData, IReadOnlyList<TItem>> items,
    Func<TData, IReadOnlyList<TItem>, TData> withItems,
    bool refetchOnMembershipChange = false) : EntityShape<TItem, TId>
{
    public override bool TryAdd(IQueryEntry entry, EntityOperation<TItem, TId> operation, QueryDataBatch batch, List<EntityWrite> writes)
    {
        if (entry is not QueryEntry<TData> typed)
        {
            return false;
        }

        var write = new EntityWrite(entry);
        writes.Add(write);
        batch.Add<TData>(typed.Key, data =>
        {
            write.Reset();
            if (data is null)
            {
                return data!;
            }

            return operation.ApplyToList(items(data), write, refetchOnMembershipChange) is { } changed ? withItems(data, changed) : data;
        }, typed);
        return true;
    }
}

internal sealed class InfiniteShape<TItem, TId, TParam, TPage>(
    Func<TPage, IReadOnlyList<TItem>> items,
    Func<TPage, IReadOnlyList<TItem>, TPage> withItems,
    bool refetchOnMembershipChange) : EntityShape<TItem, TId>
{
    public override bool TryAdd(IQueryEntry entry, EntityOperation<TItem, TId> operation, QueryDataBatch batch, List<EntityWrite> writes)
    {
        if (entry is not QueryEntry<InfiniteData<TParam, TPage>> typed)
        {
            return false;
        }

        var write = new EntityWrite(entry);
        writes.Add(write);
        batch.Add<InfiniteData<TParam, TPage>>(typed.Key, data =>
        {
            write.Reset();
            if (data is null)
            {
                return data!;
            }

            return operation.ApplyToPages(data.Pages, items, withItems, write, refetchOnMembershipChange) is { } pages
                ? data with { Pages = pages }
                : data;
        }, typed);
        return true;
    }
}

/// <summary>Detail queries: the cached data is the entity itself.</summary>
internal sealed class DetailShape<TItem, TId> : EntityShape<TItem, TId>
{
    public static DetailShape<TItem, TId> Instance { get; } = new();

    public override bool TryAdd(IQueryEntry entry, EntityOperation<TItem, TId> operation, QueryDataBatch batch, List<EntityWrite> writes)
    {
        if (entry is not QueryEntry<TItem> typed)
        {
            return false;
        }

        // Without a detail key the entry is relevant only once its data shows it holds the entity.
        var keyed = operation.Entity.DetailKey is not null;
        var write = new EntityWrite(entry) { Relevant = keyed };
        writes.Add(write);
        batch.Add<TItem>(typed.Key, data =>
        {
            write.Reset();
            if (data is null || !operation.Entity.SameId(data, operation.Id))
            {
                write.Relevant = keyed;
                return data!;
            }

            write.Relevant = true;
            return operation.ApplyToDetail(data, write, out var result) ? result : data;
        }, typed);
        return true;
    }
}
