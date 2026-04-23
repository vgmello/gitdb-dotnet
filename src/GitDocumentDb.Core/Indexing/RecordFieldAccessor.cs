using System.Text.Json.Nodes;

namespace GitDocumentDb.Indexing;

internal static class RecordFieldAccessor
{
    public static object? Read(JsonNode? root, string fieldPath)
    {
        if (root is null) return null;
        JsonNode? current = root;
        foreach (var segment in fieldPath.Split('.'))
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
                current = next;
            else return null;
        }
        return current switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<long>(out var l) => l,
            JsonValue v when v.TryGetValue<double>(out var d) => d,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => current.ToJsonString(),
        };
    }
}
