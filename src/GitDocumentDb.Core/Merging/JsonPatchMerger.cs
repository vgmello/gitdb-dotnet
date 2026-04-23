using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitDocumentDb.Merging;

public sealed class JsonPatchMerger<T> : IRecordMerger<T>
{
    private static readonly JsonSerializerOptions s_opts = new(JsonSerializerDefaults.Web);

    public MergeResult<T> Merge(T baseRecord, T local, T remote)
    {
        var baseNode = JsonSerializer.SerializeToNode(baseRecord, s_opts);
        var localNode = JsonSerializer.SerializeToNode(local, s_opts);
        var remoteNode = JsonSerializer.SerializeToNode(remote, s_opts);

        if (baseNode is null || localNode is null || remoteNode is null)
            return new MergeResult<T>(false, default, "Unsupported null top-level JSON");

        var localChanges = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var remoteChanges = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        DiffPaths(baseNode, localNode, "", localChanges);
        DiffPaths(baseNode, remoteNode, "", remoteChanges);

        foreach (var path in localChanges.Keys)
            if (remoteChanges.ContainsKey(path))
                return new MergeResult<T>(false, default, $"Overlapping change at '{path}'");

        var merged = JsonSerializer.SerializeToNode(baseRecord, s_opts)!;
        foreach (var (path, value) in localChanges) ApplyAtPath(merged, path, value);
        foreach (var (path, value) in remoteChanges) ApplyAtPath(merged, path, value);

        var mergedRecord = merged.Deserialize<T>(s_opts)!;
        return new MergeResult<T>(true, mergedRecord, null);
    }

    private static void DiffPaths(JsonNode? a, JsonNode? b, string path, Dictionary<string, JsonNode?> into)
    {
        if (JsonNodeEquals(a, b)) return;

        if (a is JsonObject aObj && b is JsonObject bObj)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in aObj) keys.Add(kv.Key);
            foreach (var kv in bObj) keys.Add(kv.Key);
            foreach (var k in keys)
            {
                aObj.TryGetPropertyValue(k, out var av);
                bObj.TryGetPropertyValue(k, out var bv);
                DiffPaths(av, bv, path.Length == 0 ? k : $"{path}.{k}", into);
            }
            return;
        }

        if (a is JsonArray aArr && b is JsonArray bArr)
        {
            var max = Math.Max(aArr.Count, bArr.Count);
            for (var i = 0; i < max; i++)
            {
                var av = i < aArr.Count ? aArr[i] : null;
                var bv = i < bArr.Count ? bArr[i] : null;
                DiffPaths(av, bv, $"{path}.{i}", into);
            }
            return;
        }

        into[path] = b?.DeepClone();
    }

    private static bool JsonNodeEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return JsonNode.DeepEquals(a, b);
    }

    private static void ApplyAtPath(JsonNode root, string path, JsonNode? value)
    {
        if (path.Length == 0) return;

        var segments = path.Split('.');
        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            current = current is JsonArray arr && int.TryParse(seg, out var idx)
                ? arr[idx]!
                : ((JsonObject)current)[seg]!;
        }
        var last = segments[^1];
        var clone = value?.DeepClone();
        if (current is JsonArray arr2 && int.TryParse(last, out var lastIdx))
            arr2[lastIdx] = clone;
        else
            ((JsonObject)current)[last] = clone;
    }
}
