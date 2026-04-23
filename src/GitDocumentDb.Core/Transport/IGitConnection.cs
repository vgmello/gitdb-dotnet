namespace GitDocumentDb.Transport;

public interface IGitConnection
{
    Task<string?> ResolveRefAsync(string refName, CancellationToken ct);
    Task<IReadOnlyList<string>> ListRefsAsync(string prefix, CancellationToken ct);

    Task<ITreeView> GetTreeAsync(string commitSha, CancellationToken ct);
    Task<ReadOnlyMemory<byte>> GetBlobAsync(string blobSha, CancellationToken ct);

    Task<string> WriteBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct);
    Task<string> WriteTreeAsync(TreeBuildSpec spec, CancellationToken ct);
    Task<string> CreateCommitAsync(CommitSpec spec, CancellationToken ct);

    Task<PushResult> UpdateRefAsync(
        string refName, string? expectedOldSha, string newSha, CancellationToken ct);

    Task<FetchResult> FetchAsync(string refName, CancellationToken ct);
}
