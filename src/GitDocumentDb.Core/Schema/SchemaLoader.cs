using System.Collections.Frozen;
using System.Text.Json;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Schema;

internal static class SchemaLoader
{
    public static async Task<DatabaseSchema> LoadAsync(IGitConnection connection, string commitSha, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(commitSha)) return DatabaseSchema.Empty;

        var tree = await connection.GetTreeAsync(commitSha, ct);
        if (!tree.TryGetBlob(".schema.json", out var blobSha))
            return DatabaseSchema.Empty;

        var bytes = await connection.GetBlobAsync(blobSha, ct);
        var file = JsonSerializer.Deserialize<SchemaFile>(bytes.Span,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var tables = new Dictionary<string, TableSchema>(StringComparer.Ordinal);
        foreach (var (name, table) in file.Tables)
        {
            var defs = new List<IndexDefinition>();
            foreach (var idx in table.Indexes)
            {
                var type = idx.Type.Equals("range", StringComparison.OrdinalIgnoreCase)
                    ? IndexType.Range : IndexType.Equality;
                defs.Add(new IndexDefinition(idx.Field, type, idx.Unique));
            }
            tables[name] = new TableSchema(name, defs);
        }
        return new DatabaseSchema(file.SchemaVersion, tables.ToFrozenDictionary(StringComparer.Ordinal));
    }
}
