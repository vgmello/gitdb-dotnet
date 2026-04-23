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

    public Task<WriteResult> PutAsync(string id, T record, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 13");
    public Task<WriteResult> DeleteAsync(string id, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 13");
    public Task<BatchResult> CommitAsync(IEnumerable<WriteOperation<T>> operations, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 14");

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
}
