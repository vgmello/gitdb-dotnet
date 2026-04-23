namespace GitDocumentDb;

public sealed record WriteOperation<T>(
    WriteOpKind Kind,
    string Id,
    T? Record,
    string? ExpectedVersion) where T : class;
