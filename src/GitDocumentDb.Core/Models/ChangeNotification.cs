namespace GitDocumentDb;

public sealed record ChangeNotification(
    string CommitSha,
    DateTimeOffset Timestamp,
    IReadOnlyList<string> ChangedPaths,
    ChangeReason Reason);
