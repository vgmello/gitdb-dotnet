using System.Buffers;
using System.Linq.Expressions;
using System.Text.Json.Nodes;
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
        var recordJson = JsonNode.Parse(bytes.Span);
        var op = new WriteExecutor.PreparedOperation(
            _name, id, path, WriteOpKind.Put, blobSha, effectiveOptions.ExpectedVersion, recordJson);
        return await WriteExecutor.ExecuteSingleAsync(_db, op, effectiveOptions, ct);
    }

    public async Task<WriteResult> DeleteAsync(string id, WriteOptions? options = null, CancellationToken ct = default)
    {
        RecordIdValidator.ThrowIfInvalid(id, nameof(id));
        var path = $"tables/{_name}/{id}{_db.Serializer.FileExtension}";
        var op = new WriteExecutor.PreparedOperation(
            _name, id, path, WriteOpKind.Delete, null, options?.ExpectedVersion, RecordJson: null);
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
                var recordJson = JsonNode.Parse(bytes.Span);
                prepared.Add(new WriteExecutor.PreparedOperation(
                    _name, op.Id, path, WriteOpKind.Put, blobSha, op.ExpectedVersion, recordJson));
            }
            else
            {
                prepared.Add(new WriteExecutor.PreparedOperation(
                    _name, op.Id, path, WriteOpKind.Delete, null, op.ExpectedVersion, RecordJson: null));
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

    public async Task<IReadOnlyList<Versioned<T>>> QueryAsync(
        Query query, ReadOptions? options = null, CancellationToken ct = default)
    {
        await MaybeFetchAsync(options, ct);
        await _db.EnsureOpenedAsync(ct);

        var snap = _db.CurrentSnapshot;
        if (!snap.Tables.TryGetValue(_name, out var table)) return Array.Empty<Versioned<T>>();

        IEnumerable<string> candidateIds;
        bool usedIndex = false;

        if (query.Predicate is not null)
        {
            var clauses = QueryCompiler.ExtractIndexClauses(query.Predicate);
            if (clauses is not null && clauses.Count > 0)
            {
                HashSet<string>? current = null;
                foreach (var clause in clauses)
                {
                    if (!table.Indexes.TryGetValue(clause.Field, out var idx)) continue;
                    var ids = ResolveClause(idx, clause);
                    if (ids is null) continue;
                    usedIndex = true;
                    current = current is null
                        ? new HashSet<string>(ids, StringComparer.Ordinal)
                        : new HashSet<string>(current.Intersect(ids), StringComparer.Ordinal);
                }
                candidateIds = current ?? (IEnumerable<string>)table.Records.Keys;
            }
            else
            {
                candidateIds = table.Records.Keys;
            }
        }
        else
        {
            candidateIds = table.Records.Keys;
        }

        if (!usedIndex && table.Records.Count > _db.Options.MaxFullScanRecordCount)
            throw new QueryException(
                $"Full-table scan on '{_name}' ({table.Records.Count} records) exceeds MaxFullScanRecordCount={_db.Options.MaxFullScanRecordCount}");

        var results = new List<Versioned<T>>();
        Func<T, bool>? compiled = query.Predicate is Expression<Func<T, bool>> typed ? typed.Compile() : null;

        foreach (var id in candidateIds)
        {
            if (!table.Records.TryGetValue(id, out var blobSha)) continue;
            var bytes = await _db.Connection.GetBlobAsync(blobSha, ct);
            var record = _db.Serializer.Deserialize<T>(bytes.Span);
            if (compiled is not null && !compiled(record)) continue;
            results.Add(new Versioned<T>(record, id, blobSha, snap.CommitSha));
        }

        if (query.OrderKey is LambdaExpression ok)
        {
            var keySelector = ok.Compile();
            results = query.OrderDescending
                ? results.OrderByDescending(r => keySelector.DynamicInvoke(r.Record)).ToList()
                : results.OrderBy(r => keySelector.DynamicInvoke(r.Record)).ToList();
        }

        if (query.SkipCount.HasValue) results = results.Skip(query.SkipCount.Value).ToList();
        if (query.TakeCount.HasValue) results = results.Take(query.TakeCount.Value).ToList();

        return results;
    }

    private static IEnumerable<string>? ResolveClause(
        GitDocumentDb.Indexing.IIndex idx,
        QueryCompiler.IndexClause clause)
    {
        if (idx is GitDocumentDb.Indexing.EqualityIndex eq && clause.Op == QueryCompiler.IndexOp.Equal)
        {
            foreach (var (k, list) in eq.ByValue)
                if (Compare(k, clause.Value) == 0)
                    return list;
            return Array.Empty<string>();
        }
        if (idx is GitDocumentDb.Indexing.UniqueEqualityIndex ueq && clause.Op == QueryCompiler.IndexOp.Equal)
        {
            foreach (var (k, recId) in ueq.ByValue)
                if (Compare(k, clause.Value) == 0)
                    return new[] { recId };
            return Array.Empty<string>();
        }
        if (idx is GitDocumentDb.Indexing.RangeIndex rng)
        {
            var matching = new List<string>();
            foreach (var key in rng.Sorted.Keys)
            {
                if (Matches(key, clause)) foreach (var recId in rng.Sorted[key]) matching.Add(recId);
            }
            return matching;
        }
        return null;
    }

    private static bool Matches(object key, QueryCompiler.IndexClause clause)
    {
        var cmp = Compare(key, clause.Value);
        return clause.Op switch
        {
            QueryCompiler.IndexOp.Equal => cmp == 0,
            QueryCompiler.IndexOp.Greater => cmp > 0,
            QueryCompiler.IndexOp.GreaterOrEqual => cmp >= 0,
            QueryCompiler.IndexOp.Less => cmp < 0,
            QueryCompiler.IndexOp.LessOrEqual => cmp <= 0,
            _ => false,
        };
    }

    private static int Compare(object a, object b)
    {
        // Normalize numeric types: promote to decimal for comparison.
        if (IsNumeric(a) && IsNumeric(b))
            return Convert.ToDecimal(a).CompareTo(Convert.ToDecimal(b));
        if (a is IComparable ac && a.GetType() == b.GetType())
            return ac.CompareTo(b);
        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    private static bool IsNumeric(object? v) =>
        v is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

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
