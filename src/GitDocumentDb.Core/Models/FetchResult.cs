namespace GitDocumentDb;

public sealed record FetchResult(
    bool HadChanges,
    string PreviousCommit,
    string CurrentCommit,
    IReadOnlyList<string> ChangedPaths);
