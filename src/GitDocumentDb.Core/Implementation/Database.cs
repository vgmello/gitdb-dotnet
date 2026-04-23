using GitDocumentDb.Transport;

namespace GitDocumentDb.Implementation;

public sealed class Database : IDatabase
{
    internal Database(string name, IGitConnection connection, IRecordSerializer serializer, DatabaseOptions options)
    {
        Name = name;
        _ = connection; _ = serializer; _ = options;
    }

    public string Name { get; }
    public string CurrentCommit => throw new NotImplementedException();
    public DateTimeOffset LastFetchedAt => throw new NotImplementedException();
    public ITable<T> GetTable<T>(string name) where T : class => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<FetchResult> FetchAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<ChangeNotification> WatchAsync(CancellationToken ct = default) => throw new NotImplementedException();
}
