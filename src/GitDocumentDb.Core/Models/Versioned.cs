namespace GitDocumentDb;

public sealed record Versioned<T>(T Record, string Id, string Version, string CommitSha) where T : class;
