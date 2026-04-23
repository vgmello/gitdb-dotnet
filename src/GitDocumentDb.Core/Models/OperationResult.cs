namespace GitDocumentDb;

public sealed record OperationResult(
    string Id,
    bool Success,
    string? NewVersion,
    ConflictInfo? Conflict,
    WriteFailureReason? FailureReason);
