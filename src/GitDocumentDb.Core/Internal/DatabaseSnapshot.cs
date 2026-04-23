using System.Collections.Frozen;

namespace GitDocumentDb.Internal;

internal sealed record DatabaseSnapshot(
    string CommitSha,
    DateTimeOffset FetchedAt,
    FrozenDictionary<string, TableSnapshot> Tables);
