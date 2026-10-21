using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DotNetQuery.Persistence;

/// <summary>
/// Trim- and NativeAOT-safe JSON serialization of <see cref="PersistedQuery"/> envelopes, for
/// <see cref="IQueryPersister"/> implementations that store records as JSON text. Uses source-generated
/// metadata; caller-supplied <see cref="JsonSerializerOptions"/> (naming policy, encoder, ...) are honored.
/// </summary>
public static class PersistedQueryJson
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Derived = new();

    /// <summary>Serializes <paramref name="query"/> to JSON.</summary>
    public static string Serialize(PersistedQuery query, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return JsonSerializer.Serialize(query, TypeInfo<PersistedQuery>(options));
    }

    /// <summary>Deserializes a <see cref="PersistedQuery"/>, or returns <see langword="null"/> for a JSON <c>null</c>.</summary>
    /// <exception cref="JsonException">The text is not a valid persisted query.</exception>
    public static PersistedQuery? Deserialize(string json, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, TypeInfo<PersistedQuery>(options));
    }

    /// <summary>Serializes a map of persisted queries keyed by storage key to JSON.</summary>
    public static string SerializeMap(IDictionary<string, PersistedQuery> map, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        var copy = map as Dictionary<string, PersistedQuery> ?? new Dictionary<string, PersistedQuery>(map, StringComparer.Ordinal);
        return JsonSerializer.Serialize(copy, TypeInfo<Dictionary<string, PersistedQuery>>(options));
    }

    /// <summary>Deserializes a map written by <see cref="SerializeMap"/>; returns an empty map for <c>null</c>.</summary>
    /// <exception cref="JsonException">The text is not a valid map.</exception>
    public static Dictionary<string, PersistedQuery> DeserializeMap(string json, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var map = JsonSerializer.Deserialize(json, TypeInfo<Dictionary<string, PersistedQuery>>(options));
        return map is null
            ? new Dictionary<string, PersistedQuery>(StringComparer.Ordinal)
            : new Dictionary<string, PersistedQuery>(map, StringComparer.Ordinal);
    }

    private static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions? options)
    {
        var effective = options is null
            ? PersistedQueryJsonContext.Default.Options
            : Derived.GetValue(options, static o => new JsonSerializerOptions(o) { TypeInfoResolver = PersistedQueryJsonContext.Default });
        return (JsonTypeInfo<T>)effective.GetTypeInfo(typeof(T));
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PersistedQuery))]
[JsonSerializable(typeof(Dictionary<string, PersistedQuery>))]
internal sealed partial class PersistedQueryJsonContext : JsonSerializerContext;
