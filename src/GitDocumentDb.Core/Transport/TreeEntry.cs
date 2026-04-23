namespace GitDocumentDb.Transport;

public enum TreeEntryKind { Blob, Tree }

public sealed record TreeEntry(string Name, string Sha, TreeEntryKind Kind);
