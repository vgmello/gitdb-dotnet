namespace GitDocumentDb.Transport;

public sealed record CommitSpec(
    string TreeSha,
    string? ParentSha,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string Message);
