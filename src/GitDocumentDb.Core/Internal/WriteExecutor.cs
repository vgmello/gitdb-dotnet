using GitDocumentDb.Implementation;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Internal;

internal static class WriteExecutor
{
    public sealed record PreparedOperation(
        string TableName,
        string Id,
        string Path,
        WriteOpKind Kind,
        string? BlobSha);

    public static async Task<WriteResult> ExecuteSingleAsync(
        Database db,
        PreparedOperation op,
        WriteOptions? options,
        CancellationToken ct)
    {
        options ??= new WriteOptions();
        await db.WriteLock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
            {
                await db.EnsureOpenedAsync(ct);
                var snap = db.CurrentSnapshot;
                var result = await TryCommitAsync(db, snap, new[] { op }, options, ct);
                if (result.success)
                {
                    var newVersion = op.Kind == WriteOpKind.Put ? op.BlobSha : null;
                    return new WriteResult(true, newVersion, result.newCommitSha, null, null);
                }
                await db.FetchAsync(ct);
                await Task.Delay(ComputeBackoff(options, attempt), ct);
            }
            return new WriteResult(false, null, null, null, WriteFailureReason.PushRejected);
        }
        finally
        {
            db.WriteLock.Release();
        }
    }

    public static async Task<BatchResult> ExecuteBatchAsync(
        Database db,
        IReadOnlyList<PreparedOperation> operations,
        WriteOptions? options,
        CancellationToken ct)
    {
        options ??= new WriteOptions();
        await db.WriteLock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
            {
                await db.EnsureOpenedAsync(ct);
                var snap = db.CurrentSnapshot;
                var result = await TryCommitAsync(db, snap, operations, options, ct);
                if (result.success)
                {
                    var ops = operations.Select(o =>
                        new OperationResult(o.Id, true,
                            o.Kind == WriteOpKind.Put ? o.BlobSha : null,
                            null, null)).ToList();
                    return new BatchResult(true, result.newCommitSha, ops);
                }
                await db.FetchAsync(ct);
                await Task.Delay(ComputeBackoff(options, attempt), ct);
            }
            var failed = operations.Select(o =>
                new OperationResult(o.Id, false, null, null, WriteFailureReason.PushRejected)).ToList();
            return new BatchResult(false, null, failed);
        }
        finally
        {
            db.WriteLock.Release();
        }
    }

    private static async Task<(bool success, string? newCommitSha)> TryCommitAsync(
        Database db,
        DatabaseSnapshot snap,
        IReadOnlyList<PreparedOperation> operations,
        WriteOptions options,
        CancellationToken ct)
    {
        // Rebuild the full tree on each write from the current snapshot, then apply mutations.
        // Real transports in Phase 4 will use base-tree diffing; the in-memory fake works
        // either way. The snapshot doesn't carry file extensions, so we re-attach the
        // serializer's extension when rebuilding. Phase 1 simplification: mixing serializers
        // across records is not supported.
        var ext = db.Serializer.FileExtension;
        var desiredEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (tableName, table) in snap.Tables)
            foreach (var (id, blobSha) in table.Records)
                desiredEntries[$"tables/{tableName}/{id}{ext}"] = blobSha;

        var anyChange = false;
        foreach (var op in operations)
        {
            if (op.Kind == WriteOpKind.Put)
            {
                if (!desiredEntries.TryGetValue(op.Path, out var existing) || existing != op.BlobSha)
                {
                    desiredEntries[op.Path] = op.BlobSha!;
                    anyChange = true;
                }
            }
            else
            {
                if (desiredEntries.Remove(op.Path)) anyChange = true;
            }
        }

        if (!anyChange)
            return (true, snap.CommitSha);

        var mutations = desiredEntries
            .Select(kv => new TreeMutation(TreeMutationKind.Upsert, kv.Key, kv.Value))
            .ToList();

        var newTree = await db.Connection.WriteTreeAsync(new TreeBuildSpec(null, mutations), ct);
        var newCommit = await db.Connection.CreateCommitAsync(
            new CommitSpec(
                newTree,
                snap.CommitSha.Length == 0 ? null : snap.CommitSha,
                options.Author ?? db.Options.DefaultAuthorName,
                db.Options.DefaultAuthorEmail,
                DateTimeOffset.UtcNow,
                options.CommitMessage ?? "gitdb write"),
            ct);

        var expectedOld = snap.CommitSha.Length == 0 ? null : snap.CommitSha;
        var push = await db.Connection.UpdateRefAsync(db.RefName, expectedOld, newCommit, ct);
        if (!push.Success) return (false, null);

        var newSnap = await SnapshotBuilder.BuildAsync(db.Connection, newCommit, ct);
        db.SwapSnapshot(newSnap);
        return (true, newCommit);
    }

    private static TimeSpan ComputeBackoff(WriteOptions options, int attempt)
    {
        var baseMs = options.RetryBackoff.TotalMilliseconds * Math.Pow(2, attempt);
        var cappedMs = Math.Min(baseMs, options.MaxRetryBackoff.TotalMilliseconds);
        var jitter = 0.5 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromMilliseconds(cappedMs * jitter);
    }
}
