using System.Collections.Frozen;
using GitDocumentDb.Indexing;

namespace GitDocumentDb.Internal;

internal sealed record TableSnapshot(
    string Name,
    FrozenDictionary<string, string> Records,
    FrozenDictionary<string, IIndex> Indexes);
