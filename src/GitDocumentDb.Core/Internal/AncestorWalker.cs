using GitDocumentDb.Transport;

namespace GitDocumentDb.Internal;

internal static class AncestorWalker
{
    public static async Task<bool> IsAncestorAsync(
        IGitConnection connection,
        string candidate,
        string descendant,
        int maxDepth,
        CancellationToken ct)
    {
        if (candidate == descendant) return true;

        var visited = new HashSet<string>(StringComparer.Ordinal) { descendant };
        var queue = new Queue<(string sha, int depth)>();
        queue.Enqueue((descendant, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (sha, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            var parents = await connection.GetCommitParentsAsync(sha, ct);
            foreach (var parent in parents)
            {
                if (parent == candidate) return true;
                if (visited.Add(parent))
                    queue.Enqueue((parent, depth + 1));
            }
        }
        return false;
    }
}
