using System.Collections.Frozen;

namespace GitDocumentDb.Internal;

internal sealed record TableSnapshot(
    string Name,
    FrozenDictionary<string, string> Records);
