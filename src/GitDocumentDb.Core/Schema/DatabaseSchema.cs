using System.Collections.Frozen;

namespace GitDocumentDb.Schema;

public sealed record DatabaseSchema(int Version, FrozenDictionary<string, TableSchema> Tables)
{
    public static DatabaseSchema Empty { get; } = new(1, FrozenDictionary<string, TableSchema>.Empty);
}
