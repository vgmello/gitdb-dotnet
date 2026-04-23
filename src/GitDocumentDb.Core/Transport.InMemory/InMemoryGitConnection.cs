using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using GitDocumentDb.Internal;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Transport.InMemory;

public sealed class InMemoryGitConnection : IGitConnection
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, (string sha, TreeEntryKind kind)>> _trees = new();
    private readonly ConcurrentDictionary<string, (string TreeSha, string? ParentSha)> _commits = new();
    private readonly ConcurrentDictionary<string, string> _refs = new();

    public Task<string?> ResolveRefAsync(string refName, CancellationToken ct)
        => Task.FromResult(_refs.TryGetValue(refName, out var sha) ? sha : null);

    public Task<IReadOnlyList<string>> ListRefsAsync(string prefix, CancellationToken ct)
    {
        IReadOnlyList<string> list = _refs.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<ITreeView> GetTreeAsync(string commitSha, CancellationToken ct)
    {
        if (!_commits.TryGetValue(commitSha, out var entry))
            throw new InvalidOperationException($"Unknown commit {commitSha}");
        var treeSha = entry.TreeSha;
        var entries = _trees.TryGetValue(treeSha, out var t) ? t : new();
        return Task.FromResult<ITreeView>(new InMemoryTreeView(commitSha, new(entries)));
    }

    public Task<ReadOnlyMemory<byte>> GetBlobAsync(string blobSha, CancellationToken ct)
    {
        if (!_blobs.TryGetValue(blobSha, out var bytes))
            throw new InvalidOperationException($"Unknown blob {blobSha}");
        return Task.FromResult<ReadOnlyMemory<byte>>(bytes);
    }

    public Task<string> WriteBlobAsync(ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var bytes = content.ToArray();
        var sha = GitBlobHasher.Hash(bytes);
        _blobs.TryAdd(sha, bytes);
        return Task.FromResult(sha);
    }

    public Task<string> WriteTreeAsync(TreeBuildSpec spec, CancellationToken ct)
    {
        Dictionary<string, (string sha, TreeEntryKind kind)> entries =
            spec.BaseTreeSha is not null && _trees.TryGetValue(spec.BaseTreeSha, out var baseEntries)
                ? new(baseEntries)
                : new();

        foreach (var m in spec.Mutations)
        {
            if (m.Kind == TreeMutationKind.Upsert)
                entries[m.Path] = (m.BlobSha!, TreeEntryKind.Blob);
            else
                entries.Remove(m.Path);
        }

        var sha = HashTree(entries);
        _trees.TryAdd(sha, entries);
        return Task.FromResult(sha);
    }

    public Task<string> CreateCommitAsync(CommitSpec spec, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("tree ").Append(spec.TreeSha).Append('\n');
        if (spec.ParentSha is not null) sb.Append("parent ").Append(spec.ParentSha).Append('\n');
        sb.Append("author ").Append(spec.AuthorName).Append(' ')
          .Append('<').Append(spec.AuthorEmail).Append("> ")
          .Append(spec.AuthorDate.ToUnixTimeSeconds()).Append('\n');
        sb.Append('\n').Append(spec.Message);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA1.HashData(bytes);
        var commitSha = Convert.ToHexStringLower(hash);
        _commits.TryAdd(commitSha, (spec.TreeSha, spec.ParentSha));
        return Task.FromResult(commitSha);
    }

    public Task<PushResult> UpdateRefAsync(
        string refName, string? expectedOldSha, string newSha, CancellationToken ct)
    {
        while (true)
        {
            var currentExists = _refs.TryGetValue(refName, out var current);
            var matches = (expectedOldSha, currentExists) switch
            {
                (null, false) => true,
                (not null, true) => expectedOldSha == current,
                _ => false,
            };
            if (!matches)
                return Task.FromResult(new PushResult(false, null, PushRejectReason.NonFastForward));

            bool ok;
            if (currentExists)
                ok = _refs.TryUpdate(refName, newSha, current!);
            else
                ok = _refs.TryAdd(refName, newSha);
            if (ok)
                return Task.FromResult(new PushResult(true, newSha, null));
        }
    }

    public async Task<FetchResult> FetchAsync(string refName, CancellationToken ct)
    {
        var sha = await ResolveRefAsync(refName, ct);
        return new FetchResult(false, sha ?? "", sha ?? "", Array.Empty<string>());
    }

    public Task<IReadOnlyList<string>> GetCommitParentsAsync(string commitSha, CancellationToken ct)
    {
        if (!_commits.TryGetValue(commitSha, out var entry))
            throw new InvalidOperationException($"Unknown commit {commitSha}");
        IReadOnlyList<string> parents = entry.ParentSha is null
            ? Array.Empty<string>()
            : new[] { entry.ParentSha };
        return Task.FromResult(parents);
    }

    private static string HashTree(Dictionary<string, (string sha, TreeEntryKind kind)> entries)
    {
        var sb = new StringBuilder();
        foreach (var kv in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append(' ').Append(kv.Value.sha)
              .Append(' ').Append(kv.Value.kind).Append('\n');
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
