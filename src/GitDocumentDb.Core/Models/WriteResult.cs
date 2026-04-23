namespace GitDocumentDb;

public sealed record WriteResult(
    bool Success,
    string? NewVersion,
    string? NewCommitSha,
    ConflictInfo? Conflict,
    WriteFailureReason? FailureReason);
