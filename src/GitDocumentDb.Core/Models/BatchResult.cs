namespace GitDocumentDb;

public sealed record BatchResult(
    bool Success,
    string? NewCommitSha,
    IReadOnlyList<OperationResult> Operations);
