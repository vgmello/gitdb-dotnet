using System.Text.Json.Nodes;
using GitDocumentDb.Implementation;
using GitDocumentDb.Indexing;
using GitDocumentDb.Schema;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Internal;

internal static class WriteExecutor
{
    public sealed record PreparedOperation(
        string TableName,
        string Id,
        string Path,
        WriteOpKind Kind,
        string? BlobSha,
        string? ExpectedVersion,
        JsonNode? RecordJson);

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
                if (result.conflict is not null)
                {
                    return new WriteResult(false, null, null, result.conflict, null);
                }
                // Push rejection — fetch + retry.
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
                if (result.conflict is not null)
                {
                    var ops = operations.Select(o =>
                        o.Id == result.conflictOpId
                            ? new OperationResult(o.Id, false, null, result.conflict, null)
                            : new OperationResult(o.Id, false, null, null, null))
                        .ToList();
                    return new BatchResult(false, null, ops);
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

    private static async Task<(bool success, string? newCommitSha, string? conflictOpId, ConflictInfo? conflict)> TryCommitAsync(
        Database db,
        DatabaseSnapshot snap,
        IReadOnlyList<PreparedOperation> operations,
        WriteOptions options,
        CancellationToken ct)
    {
        // OptimisticReject: per-op version check.
        if (options.Mode == ConcurrencyMode.OptimisticReject)
        {
            foreach (var op in operations)
            {
                var currentVersion = LookupCurrentVersion(snap, op.TableName, op.Id);
                var conflict = CheckReject(op, currentVersion);
                if (conflict is not null)
                    return (false, null, op.Id, conflict);
            }
        }

        // Unique index enforcement (independent of concurrency mode).
        foreach (var op in operations)
        {
            if (op.Kind != WriteOpKind.Put || op.RecordJson is null) continue;
            if (!snap.Tables.TryGetValue(op.TableName, out var table)) continue;
            if (!snap.Schema.Tables.TryGetValue(op.TableName, out var tableSchema)) continue;

            foreach (var idxDef in tableSchema.Indexes)
            {
                if (!idxDef.Unique || idxDef.Type != IndexType.Equality) continue;

                var newValue = RecordFieldAccessor.Read(op.RecordJson, idxDef.Field);
                if (newValue is null) continue;

                if (table.Indexes.TryGetValue(idxDef.Field, out var indexObj)
                    && indexObj is UniqueEqualityIndex uidx)
                {
                    if (uidx.ByValue.TryGetValue(newValue, out var owner) && owner != op.Id)
                    {
                        var conflict = new ConflictInfo(
                            op.Path, op.ExpectedVersion ?? "", owner, null,
                            ConflictReason.UniqueViolation);
                        return (false, null, op.Id, conflict);
                    }
                }
            }
        }

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

        // Preserve non-table entries (e.g. .schema.json) from the current tree.
        if (snap.CommitSha.Length > 0)
        {
            var currentTree = await db.Connection.GetTreeAsync(snap.CommitSha, ct);
            foreach (var entry in currentTree.EnumerateChildren(""))
            {
                if (entry.Kind != TreeEntryKind.Blob) continue;
                if (currentTree.TryGetBlob(entry.Name, out var sha))
                    desiredEntries.TryAdd(entry.Name, sha);
            }
        }

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
            return (true, snap.CommitSha, null, null);

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
        if (!push.Success) return (false, null, null, null);

        var newSnap = await SnapshotBuilder.BuildAsync(db.Connection, newCommit, ct);
        db.SwapSnapshot(newSnap);
        return (true, newCommit, null, null);
    }

    private static string? LookupCurrentVersion(DatabaseSnapshot snap, string tableName, string id)
    {
        if (!snap.Tables.TryGetValue(tableName, out var table)) return null;
        return table.Records.TryGetValue(id, out var sha) ? sha : null;
    }

    private static ConflictInfo? CheckReject(PreparedOperation op, string? currentVersion)
    {
        if (op.Kind == WriteOpKind.Put && op.ExpectedVersion == Versions.Absent && currentVersion is not null)
            return new ConflictInfo(op.Path, Versions.Absent, currentVersion, null, ConflictReason.ExpectedAbsentButPresent);

        if (op.Kind == WriteOpKind.Put && op.ExpectedVersion is not null && op.ExpectedVersion != Versions.Absent)
        {
            if (currentVersion != op.ExpectedVersion)
                return new ConflictInfo(op.Path, op.ExpectedVersion, currentVersion ?? "", null, ConflictReason.VersionMismatch);
        }

        if (op.Kind == WriteOpKind.Delete && op.ExpectedVersion is not null)
        {
            if (op.ExpectedVersion == Versions.Absent)
                return new ConflictInfo(op.Path, Versions.Absent, currentVersion ?? "", null, ConflictReason.VersionMismatch);
            if (currentVersion is null)
                return new ConflictInfo(op.Path, op.ExpectedVersion, "", null, ConflictReason.ExpectedPresentButAbsent);
            if (currentVersion != op.ExpectedVersion)
                return new ConflictInfo(op.Path, op.ExpectedVersion, currentVersion, null, ConflictReason.VersionMismatch);
        }

        return null;
    }

    private static TimeSpan ComputeBackoff(WriteOptions options, int attempt)
    {
        var baseMs = options.RetryBackoff.TotalMilliseconds * Math.Pow(2, attempt);
        var cappedMs = Math.Min(baseMs, options.MaxRetryBackoff.TotalMilliseconds);
        var jitter = 0.5 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromMilliseconds(cappedMs * jitter);
    }
}
