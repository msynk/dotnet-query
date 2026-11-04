using System.Text.Json;

namespace DotNetQuery.Blazor.Tests;

/// <summary>Reads and writes the prerender hydration payload: a JSON string <c>{"updatedAt":...,"data":...}</c>.</summary>
internal static class HydrationEnvelope
{
    private static readonly JsonSerializerOptions Web = CreateOptions();

    public static string Create<T>(T data, DateTimeOffset updatedAt) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["updatedAt"] = updatedAt, ["data"] = data }, Web);

    public static T? Data<T>(string envelope)
    {
        using var document = JsonDocument.Parse(envelope);
        return document.RootElement.GetProperty("data").Deserialize<T>(Web);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
