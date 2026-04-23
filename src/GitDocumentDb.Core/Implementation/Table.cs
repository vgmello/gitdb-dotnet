using System.Buffers;
using GitDocumentDb.Internal;

namespace GitDocumentDb.Implementation;

internal sealed class Table<T> : ITable<T> where T : class
{
    private readonly Database _db;
    private readonly string _name;

    internal Table(Database db, string name)
    {
        _db = db;
        _name = name;
    }

    public async ValueTask<Versioned<T>?> GetAsync(string id, ReadOptions? options = null, CancellationToken ct = default)
    {
        RecordIdValidator.ThrowIfInvalid(id, nameof(id));
        await MaybeFetchAsync(options, ct);
        await _db.EnsureOpenedAsync(ct);

        var snap = _db.CurrentSnapshot;
        if (!snap.Tables.TryGetValue(_name, out var table)) return null;
        if (!table.Records.TryGetValue(id, out var blobSha)) return null;

        var bytes = await _db.Connection.GetBlobAsync(blobSha, ct);
        var record = _db.Serializer.Deserialize<T>(bytes.Span);
        return new Versioned<T>(record, id, blobSha, snap.CommitSha);
    }

    public async Task<WriteResult> PutAsync(string id, T record, WriteOptions? options = null, CancellationToken ct = default)
    {
        RecordIdValidator.ThrowIfInvalid(id, nameof(id));
        ArgumentNullException.ThrowIfNull(record);

        options ??= new WriteOptions();
        var effectiveRecord = record;
        var effectiveOptions = options;
        var path = $"tables/{_name}/{id}{_db.Serializer.FileExtension}";

        if (options.Mode == ConcurrencyMode.OptimisticMerge
            && options.ExpectedVersion is not null
            && options.ExpectedVersion != Versions.Absent)
        {
            await _db.EnsureOpenedAsync(ct);
            var snap = _db.CurrentSnapshot;
            var current = LookupCurrentVersion(snap, id);
            if (current is not null && current != options.ExpectedVersion)
            {
                var merge = await TryMergeAsync(id, record, options.ExpectedVersion, current, ct);
                if (merge.Conflict is not null)
                    return new WriteResult(false, null, null, merge.Conflict, null);
                if (merge.MergedRecord is not null)
                    effectiveRecord = merge.MergedRecord;

                // Re-read current as the effective expected version for push-time CAS.
                // Promote to OptimisticReject — the merge is already resolved.
                effectiveOptions = new WriteOptions
                {
                    Mode = ConcurrencyMode.OptimisticReject,
                    ExpectedVersion = current,
                    MaxRetries = options.MaxRetries,
                    RetryBackoff = options.RetryBackoff,
                    MaxRetryBackoff = options.MaxRetryBackoff,
                    Author = options.Author,
                    CommitMessage = options.CommitMessage,
                };
            }
            else if (current is null)
            {
                // Record disappeared between read and write. Promote to reject with Absent.
                effectiveOptions = new WriteOptions
                {
                    Mode = ConcurrencyMode.OptimisticReject,
                    ExpectedVersion = Versions.Absent,
                    MaxRetries = options.MaxRetries,
                    RetryBackoff = options.RetryBackoff,
                    MaxRetryBackoff = options.MaxRetryBackoff,
                    Author = options.Author,
                    CommitMessage = options.CommitMessage,
                };
            }
        }

        var writer = new ArrayBufferWriter<byte>();
        _db.Serializer.Serialize(effectiveRecord, writer);
        var bytes = writer.WrittenMemory;
        if (bytes.Length > _db.Options.RecordSizeHardLimitBytes)
            return new WriteResult(false, null, null, null, WriteFailureReason.RecordTooLarge);

        var blobSha = await _db.Connection.WriteBlobAsync(bytes, ct);
        var op = new WriteExecutor.PreparedOperation(
            _name, id, path, WriteOpKind.Put, blobSha, effectiveOptions.ExpectedVersion);
        return await WriteExecutor.ExecuteSingleAsync(_db, op, effectiveOptions, ct);
    }

    public async Task<WriteResult> DeleteAsync(string id, WriteOptions? options = null, CancellationToken ct = default)
    {
        RecordIdValidator.ThrowIfInvalid(id, nameof(id));
        var path = $"tables/{_name}/{id}{_db.Serializer.FileExtension}";
        var op = new WriteExecutor.PreparedOperation(
            _name, id, path, WriteOpKind.Delete, null, options?.ExpectedVersion);
        return await WriteExecutor.ExecuteSingleAsync(_db, op, options, ct);
    }

    public async Task<BatchResult> CommitAsync(
        IEnumerable<WriteOperation<T>> operations,
        WriteOptions? options = null,
        CancellationToken ct = default)
    {
        var opList = operations.ToList();
        var prepared = new List<WriteExecutor.PreparedOperation>(opList.Count);
        var failures = new Dictionary<string, OperationResult>(StringComparer.Ordinal);

        foreach (var op in opList)
        {
            RecordIdValidator.ThrowIfInvalid(op.Id, "operation.Id");
            var path = $"tables/{_name}/{op.Id}{_db.Serializer.FileExtension}";
            if (op.Kind == WriteOpKind.Put)
            {
                if (op.Record is null)
                    throw new ArgumentNullException("operation.Record");
                var writer = new ArrayBufferWriter<byte>();
                _db.Serializer.Serialize(op.Record, writer);
                var bytes = writer.WrittenMemory;
                if (bytes.Length > _db.Options.RecordSizeHardLimitBytes)
                {
                    failures[op.Id] = new OperationResult(op.Id, false, null, null, WriteFailureReason.RecordTooLarge);
                    continue;
                }
                var blobSha = await _db.Connection.WriteBlobAsync(bytes, ct);
                prepared.Add(new WriteExecutor.PreparedOperation(
                    _name, op.Id, path, WriteOpKind.Put, blobSha, op.ExpectedVersion));
            }
            else
            {
                prepared.Add(new WriteExecutor.PreparedOperation(
                    _name, op.Id, path, WriteOpKind.Delete, null, op.ExpectedVersion));
            }
        }

        if (failures.Count > 0)
        {
            var all = opList.Select(o =>
                failures.TryGetValue(o.Id, out var f)
                    ? f
                    : new OperationResult(o.Id, false, null, null, null))
                .ToList();
            return new BatchResult(false, null, all);
        }

        return await WriteExecutor.ExecuteBatchAsync(_db, prepared, options, ct);
    }

    private async ValueTask MaybeFetchAsync(ReadOptions? options, CancellationToken ct)
    {
        if (options is null) return;
        if (options.FetchFirst ||
            (options.MaxStaleness.HasValue &&
             DateTimeOffset.UtcNow - _db.LastFetchedAt > options.MaxStaleness.Value))
        {
            await _db.FetchAsync(ct);
        }
    }

    private string? LookupCurrentVersion(DatabaseSnapshot snap, string id)
    {
        if (!snap.Tables.TryGetValue(_name, out var table)) return null;
        return table.Records.TryGetValue(id, out var sha) ? sha : null;
    }

    private async Task<(T? MergedRecord, ConflictInfo? Conflict)> TryMergeAsync(
        string id, T local, string baseVersion, string currentVersion, CancellationToken ct)
    {
        if (!_db.Options.RecordMergers.TryGetValue(typeof(T), out var mergerObj)
            || mergerObj is not IRecordMerger<T> merger)
        {
            return (default, new ConflictInfo(
                $"tables/{_name}/{id}{_db.Serializer.FileExtension}",
                baseVersion, currentVersion, null, ConflictReason.VersionMismatch));
        }

        var baseBytes = await _db.Connection.GetBlobAsync(baseVersion, ct);
        var currentBytes = await _db.Connection.GetBlobAsync(currentVersion, ct);
        var baseRecord = _db.Serializer.Deserialize<T>(baseBytes.Span);
        var remoteRecord = _db.Serializer.Deserialize<T>(currentBytes.Span);

        var result = merger.Merge(baseRecord, local, remoteRecord);
        if (!result.Succeeded)
        {
            return (default, new ConflictInfo(
                $"tables/{_name}/{id}{_db.Serializer.FileExtension}",
                baseVersion, currentVersion, currentBytes, ConflictReason.UnmergeableChange));
        }
        return (result.Merged, null);
    }
}
