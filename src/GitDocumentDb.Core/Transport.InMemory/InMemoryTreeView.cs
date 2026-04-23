using GitDocumentDb.Transport;

namespace GitDocumentDb.Transport.InMemory;

internal sealed class InMemoryTreeView : ITreeView
{
    private readonly Dictionary<string, (string sha, TreeEntryKind kind)> _entries;

    public InMemoryTreeView(string commitSha, Dictionary<string, (string, TreeEntryKind)> entries)
    {
        CommitSha = commitSha;
        _entries = entries;
    }

    public string CommitSha { get; }

    public bool TryGetBlob(string path, out string blobSha)
    {
        if (_entries.TryGetValue(path, out var e) && e.kind == TreeEntryKind.Blob)
        {
            blobSha = e.sha;
            return true;
        }
        blobSha = "";
        return false;
    }

    public bool TryGetTree(string path, out ITreeView subtree)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        var filtered = new Dictionary<string, (string, TreeEntryKind)>();
        foreach (var (k, v) in _entries)
            if (k.StartsWith(prefix, StringComparison.Ordinal))
                filtered[k[prefix.Length..]] = v;
        if (filtered.Count == 0)
        {
            subtree = null!;
            return false;
        }
        subtree = new InMemoryTreeView(CommitSha, filtered);
        return true;
    }

    public IEnumerable<TreeEntry> EnumerateChildren(string path)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        var direct = new Dictionary<string, (string sha, TreeEntryKind kind)>();
        foreach (var (key, value) in _entries)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = key[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                direct[rest] = value;
            }
            else
            {
                var dirName = rest[..slash];
                direct.TryAdd(dirName, ("", TreeEntryKind.Tree));
            }
        }
        return direct.Select(kv => new TreeEntry(kv.Key, kv.Value.sha, kv.Value.kind));
    }
}
