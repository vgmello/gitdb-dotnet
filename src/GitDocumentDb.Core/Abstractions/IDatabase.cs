namespace GitDocumentDb;

public interface IDatabase
{
    string Name { get; }
    string CurrentCommit { get; }
    DateTimeOffset LastFetchedAt { get; }

    ITable<T> GetTable<T>(string name) where T : class;
    Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken ct = default);

    Task<FetchResult> FetchAsync(CancellationToken ct = default);
    IAsyncEnumerable<ChangeNotification> WatchAsync(CancellationToken ct = default);
}
