using System.Collections.Frozen;
using GitDocumentDb.Schema;

namespace GitDocumentDb.Internal;

internal sealed record DatabaseSnapshot(
    string CommitSha,
    DateTimeOffset FetchedAt,
    DatabaseSchema Schema,
    FrozenDictionary<string, TableSnapshot> Tables);
