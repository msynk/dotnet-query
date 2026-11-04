using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetQuery.Blazor;

/// <summary>
/// Transfers query data from a prerender to the interactive render through Blazor's
/// <see cref="PersistentComponentState"/>, keeping each query's original timestamp so staleness is preserved.
/// </summary>
/// <remarks>
/// <para>
/// Blazor prerenders in a different DI scope (and, for WebAssembly, a different process) than the
/// interactive render, so the interactive <see cref="IQueryClient"/> starts empty. Data persisted here is
/// embedded in the prerendered HTML and hydrated into the interactive client before its first fetch.
/// </para>
/// <para>
/// The <see cref="JsonTypeInfo{T}"/> overloads are trim- and NativeAOT-safe. <c>&lt;Query&gt;</c> and
/// <c>DotNetQueryComponentBase</c> use the source-generated metadata registered with
/// <c>AddDotNetQueryBlazor().UseHydrationJson(AppJson.Default)</c>, and reflection-based JSON for types it does not cover.
/// </para>
/// </remarks>
public static class QueryStateHydration
{
    private const string Prefix = "dnq:";
    private const string ReflectionMessage = "Serializes TData with reflection-based JSON. Use the JsonTypeInfo<TData> overload in trimmed or NativeAOT apps.";

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
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static bool Persist<TData>(PersistentComponentState state, IQueryClient client, QueryKey key) =>
        Persist(state, client, key, ReflectionTypeInfo<TData>());

    /// <summary>
    /// Persists the cached data of <paramref name="key"/> (if any) into <paramref name="state"/>, serialized with
    /// <paramref name="typeInfo"/>. Trim- and NativeAOT-safe.
    /// </summary>
    /// <returns><see langword="true"/> if the data was persisted.</returns>
    public static bool Persist<TData>(PersistentComponentState state, IQueryClient client, QueryKey key, JsonTypeInfo<TData> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var result = client.GetQueryData<TData>(key);
        if (!result.IsSuccess || result.DataUpdatedAt is not { } updatedAt)
        {
            return false;
        }

        try
        {
            PersistString(state, StateKey(key), Encoding.UTF8.GetString(WriteEnvelope(result.Data, updatedAt, typeInfo)));
            return true;
        }
        catch (ArgumentException)
        {
            return false; // already persisted by another component rendering the same query
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException)
        {
            return false; // not JSON-serializable: the interactive render simply fetches it again
        }
    }

    /// <summary>
    /// Hydrates <paramref name="client"/> with data persisted for <paramref name="key"/> during prerendering,
    /// keeping its original timestamp. Only applies data newer than what the client already holds.
    /// </summary>
    /// <returns><see langword="true"/> if data was hydrated.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static bool TryHydrate<TData>(PersistentComponentState state, IQueryClient client, QueryKey key) =>
        TryHydrate(state, client, key, ReflectionTypeInfo<TData>());

    /// <summary>
    /// Hydrates <paramref name="client"/> with data persisted for <paramref name="key"/> during prerendering,
    /// deserialized with <paramref name="typeInfo"/>. Trim- and NativeAOT-safe.
    /// </summary>
    /// <returns><see langword="true"/> if data was hydrated.</returns>
    public static bool TryHydrate<TData>(PersistentComponentState state, IQueryClient client, QueryKey key, JsonTypeInfo<TData> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(typeInfo);

        try
        {
            if (!TryTakeString(state, StateKey(key), out var text) || text is null)
            {
                return false;
            }

            return TryReadEnvelope(Encoding.UTF8.GetBytes(text), typeInfo, out var data, out var updatedAt)
                && client.HydrateQueryData(key, data!, updatedAt);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException or FormatException)
        {
            return false; // incompatible payload: fall back to fetching
        }
    }

    // Used by the components: the registered source-generated metadata, else reflection (see UseHydrationJson).
    internal static bool PersistFor<TData>(PersistentComponentState state, IQueryClient client, QueryKey key, IServiceProvider? services) =>
        TypeInfoFor<TData>(services) is { } typeInfo && Persist(state, client, key, typeInfo);

    internal static bool TryHydrateFor<TData>(PersistentComponentState state, IQueryClient client, QueryKey key, IServiceProvider? services) =>
        TypeInfoFor<TData>(services) is { } typeInfo && TryHydrate(state, client, key, typeInfo);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification =
        "Reflection is the fallback for types the app did not register with UseHydrationJson. Blazor WebAssembly's default " +
        "partial trimming keeps the app's own types; apps that trim them register source-generated metadata, and a payload " +
        "that cannot be (de)serialized is skipped (the interactive render fetches it instead).")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "See IL2026.")]
    private static JsonTypeInfo<TData>? TypeInfoFor<TData>(IServiceProvider? services)
    {
        try
        {
            if (services?.GetService<QueryHydrationJson>() is { } registered &&
                registered.Options.TypeInfoResolver?.GetTypeInfo(typeof(TData), registered.Options) is JsonTypeInfo<TData> generated)
            {
                return generated;
            }

            return ReflectionTypeInfo<TData>();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return null; // reflection disabled in this app and no metadata registered: hydration is skipped
        }
    }

    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    private static JsonTypeInfo<TData> ReflectionTypeInfo<TData>() => (JsonTypeInfo<TData>)ReflectionOptions.Value.GetTypeInfo(typeof(TData));

    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    private static class ReflectionOptions
    {
        public static readonly JsonSerializerOptions Value = Create();

        private static JsonSerializerOptions Create()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
            options.MakeReadOnly();
            return options;
        }
    }

    // The envelope travels as a JSON string: strings need no reflection metadata, so these calls are trim-safe.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "A string payload is serialized by the built-in string converter.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "A string payload is serialized by the built-in string converter.")]
    private static void PersistString(PersistentComponentState state, string key, string value) => state.PersistAsJson(key, value);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "A string payload is deserialized by the built-in string converter.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "A string payload is deserialized by the built-in string converter.")]
    private static bool TryTakeString(PersistentComponentState state, string key, out string? value) => state.TryTakeFromJson(key, out value);

    // {"updatedAt":"...","data":...}: the envelope is written by hand, so only TData needs (generated or reflection) metadata.
    private static byte[] WriteEnvelope<TData>(TData? data, DateTimeOffset updatedAt, JsonTypeInfo<TData> typeInfo)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("updatedAt", updatedAt);
            writer.WritePropertyName("data");
            JsonSerializer.Serialize(writer, data!, typeInfo);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static bool TryReadEnvelope<TData>(byte[] envelope, JsonTypeInfo<TData> typeInfo, out TData? data, out DateTimeOffset updatedAt)
    {
        data = default;
        updatedAt = default;
        using var document = JsonDocument.Parse(envelope);
        if (!document.RootElement.TryGetProperty("updatedAt", out var timestamp) || !timestamp.TryGetDateTimeOffset(out updatedAt) ||
            !document.RootElement.TryGetProperty("data", out var payload))
        {
            return false;
        }

        data = payload.Deserialize(typeInfo);
        return true;
    }
}

/// <summary>The JSON metadata registered with <c>UseHydrationJson</c> for prerender hydration.</summary>
internal sealed class QueryHydrationJson(JsonSerializerOptions options)
{
    public JsonSerializerOptions Options { get; } = options;
}
