using System.Collections.Frozen;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Internal;

internal static class SnapshotBuilder
{
    public static async Task<DatabaseSnapshot> BuildAsync(
        IGitConnection connection,
        string commitSha,
        CancellationToken ct)
    {
        var tree = await connection.GetTreeAsync(commitSha, ct);
        var tables = new Dictionary<string, TableSnapshot>(StringComparer.Ordinal);

        foreach (var tableEntry in tree.EnumerateChildren("tables"))
        {
            if (tableEntry.Kind != TreeEntryKind.Tree) continue;
            var records = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var recordEntry in tree.EnumerateChildren($"tables/{tableEntry.Name}"))
            {
                if (recordEntry.Kind != TreeEntryKind.Blob) continue;
                if (!tree.TryGetBlob($"tables/{tableEntry.Name}/{recordEntry.Name}", out var sha)) continue;
                var id = StripExtension(recordEntry.Name);
                records[id] = sha;
            }
            tables[tableEntry.Name] = new TableSnapshot(
                tableEntry.Name,
                records.ToFrozenDictionary(StringComparer.Ordinal));
        }

        return new DatabaseSnapshot(
            commitSha,
            DateTimeOffset.UtcNow,
            tables.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static string StripExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 ? fileName : fileName[..dot];
    }
}
