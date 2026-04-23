namespace GitDocumentDb.Implementation;

internal sealed class Table<T> : ITable<T> where T : class
{
    internal Table(Database database, string name) { _ = database; _ = name; }

    public ValueTask<Versioned<T>?> GetAsync(string id, ReadOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 12");
    public Task<WriteResult> PutAsync(string id, T record, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 13");
    public Task<WriteResult> DeleteAsync(string id, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 13");
    public Task<BatchResult> CommitAsync(IEnumerable<WriteOperation<T>> operations, WriteOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 14");
}
