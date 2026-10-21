using Microsoft.AspNetCore.Components;

namespace DotNetQuery.Blazor;

/// <summary>
/// Transfers query data from a prerender to the interactive render through Blazor's
/// <see cref="PersistentComponentState"/>, keeping each query's original timestamp so staleness is preserved.
/// </summary>
/// <remarks>
/// Blazor prerenders in a different DI scope (and, for WebAssembly, a different process) than the
/// interactive render, so the interactive <see cref="IQueryClient"/> starts empty. Data persisted here is
/// embedded in the prerendered HTML and hydrated into the interactive client before its first fetch.
/// </remarks>
public static class QueryStateHydration
{
    private const string Prefix = "dnq:";

    /// <summary>Gets the persisted-state key used for <paramref name="key"/>.</summary>
    public static string StateKey(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Prefix + key;
    }

    /// <summary>
    /// Persists the cached data of <paramref name="key"/> (if any) into <paramref name="state"/>. Call from a
    /// <see cref="PersistentComponentState.RegisterOnPersisting(Func{Task})"/> callback. Duplicate keys
    /// (several components rendering the same query) and non-serializable data are ignored.
    /// </summary>
    /// <returns><see langword="true"/> if the data was persisted.</returns>
    public static bool Persist<TData>(PersistentComponentState state, IQueryClient client, QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(key);

        var result = client.GetQueryData<TData>(key);
        if (!result.IsSuccess || result.DataUpdatedAt is not { } updatedAt)
        {
            return false;
        }

        try
        {
            state.PersistAsJson(StateKey(key), new HydratedQuery<TData> { Data = result.Data, UpdatedAt = updatedAt });
            return true;
        }
        catch (ArgumentException)
        {
            return false; // already persisted by another component rendering the same query
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false; // not JSON-serializable: the interactive render simply fetches it again
        }
    }

    /// <summary>
    /// Hydrates <paramref name="client"/> with data persisted for <paramref name="key"/> during prerendering,
    /// keeping its original timestamp. Only applies data newer than what the client already holds.
    /// </summary>
    /// <returns><see langword="true"/> if data was hydrated.</returns>
    public static bool TryHydrate<TData>(PersistentComponentState state, IQueryClient client, QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(key);

        try
        {
            return state.TryTakeFromJson<HydratedQuery<TData>>(StateKey(key), out var hydrated)
                && hydrated is not null
                && client.HydrateQueryData(key, hydrated.Data!, hydrated.UpdatedAt);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false; // incompatible payload: fall back to fetching
        }
    }

    /// <summary>The persisted envelope: the data plus the time it was fetched on the server.</summary>
    internal sealed class HydratedQuery<TData>
    {
        public TData? Data { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
