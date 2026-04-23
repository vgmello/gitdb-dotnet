namespace GitDocumentDb.Transport;

public interface ITreeView
{
    string CommitSha { get; }
    bool TryGetBlob(string path, out string blobSha);
    bool TryGetTree(string path, out ITreeView subtree);
    IEnumerable<TreeEntry> EnumerateChildren(string path);
}
