using System.Collections.Frozen;
using System.Text.Json.Nodes;
using GitDocumentDb.Schema;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Indexing;

internal static class IndexBuilder
{
    public sealed record BuildResult(FrozenDictionary<string, IIndex> Indexes, string? UniqueViolation);

    public static async Task<BuildResult> BuildAsync(
        IGitConnection connection,
        TableSchema schema,
        IReadOnlyDictionary<string, string> records,
        CancellationToken ct)
    {
        if (schema.Indexes.Count == 0 || records.Count == 0)
            return new BuildResult(FrozenDictionary<string, IIndex>.Empty, null);

        var parsed = new Dictionary<string, JsonNode>(records.Count, StringComparer.Ordinal);
        foreach (var (id, sha) in records)
        {
            var bytes = await connection.GetBlobAsync(sha, ct);
            var node = JsonNode.Parse(bytes.Span);
            if (node is not null) parsed[id] = node;
        }

        var builders = new Dictionary<string, IIndex>(StringComparer.Ordinal);
        foreach (var def in schema.Indexes)
        {
            if (def.Type == IndexType.Equality && def.Unique)
            {
                var map = new Dictionary<object, string>();
                foreach (var (id, node) in parsed)
                {
                    var value = RecordFieldAccessor.Read(node, def.Field);
                    if (value is null) continue;
                    if (map.TryGetValue(value, out var existing))
                        return new BuildResult(FrozenDictionary<string, IIndex>.Empty,
                            $"unique index '{def.Field}' violation: '{value}' in records '{existing}' and '{id}'");
                    map[value] = id;
                }
                builders[def.Field] = new UniqueEqualityIndex(def.Field, map);
            }
            else if (def.Type == IndexType.Equality)
            {
                var map = new Dictionary<object, List<string>>();
                foreach (var (id, node) in parsed)
                {
                    var value = RecordFieldAccessor.Read(node, def.Field);
                    if (value is null) continue;
                    if (!map.TryGetValue(value, out var list)) map[value] = list = new();
                    list.Add(id);
                }
                builders[def.Field] = new EqualityIndex(def.Field, map);
            }
            else // Range
            {
                var sorted = new SortedList<object, List<string>>(Comparer<object>.Create(CompareValues));
                foreach (var (id, node) in parsed)
                {
                    var value = RecordFieldAccessor.Read(node, def.Field);
                    if (value is null) continue;
                    if (!sorted.TryGetValue(value, out var list)) sorted[value] = list = new();
                    list.Add(id);
                }
                builders[def.Field] = new RangeIndex(def.Field, sorted);
            }
        }
        return new BuildResult(builders.ToFrozenDictionary(StringComparer.Ordinal), null);
    }

    private static int CompareValues(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a is IComparable ac && a.GetType() == b.GetType()) return ac.CompareTo(b);
        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }
}
