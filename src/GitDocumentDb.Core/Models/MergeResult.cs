namespace GitDocumentDb;

public sealed record MergeResult<T>(bool Succeeded, T? Merged, string? ConflictDescription);
