namespace GitDocumentDb;

public interface ITable<T> where T : class
{
    ValueTask<Versioned<T>?> GetAsync(string id, ReadOptions? options = null, CancellationToken ct = default);

    Task<WriteResult> PutAsync(string id, T record, WriteOptions? options = null, CancellationToken ct = default);
    Task<WriteResult> DeleteAsync(string id, WriteOptions? options = null, CancellationToken ct = default);

    Task<BatchResult> CommitAsync(IEnumerable<WriteOperation<T>> operations, WriteOptions? options = null, CancellationToken ct = default);

    Task<IReadOnlyList<Versioned<T>>> QueryAsync(Query query, ReadOptions? options = null, CancellationToken ct = default);
}
