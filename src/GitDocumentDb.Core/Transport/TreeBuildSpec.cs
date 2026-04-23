namespace GitDocumentDb.Transport;

public sealed record TreeBuildSpec(
    string? BaseTreeSha,
    IReadOnlyList<TreeMutation> Mutations);

public sealed record TreeMutation(
    TreeMutationKind Kind,
    string Path,
    string? BlobSha);

public enum TreeMutationKind { Upsert, Delete }
