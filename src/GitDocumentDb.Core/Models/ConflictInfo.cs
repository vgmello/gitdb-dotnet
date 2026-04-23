namespace GitDocumentDb;

public sealed record ConflictInfo(
    string Path,
    string ExpectedVersion,
    string ActualVersion,
    ReadOnlyMemory<byte>? CurrentContent,
    ConflictReason Reason);
