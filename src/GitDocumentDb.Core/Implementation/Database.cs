using System.Collections.Frozen;
using GitDocumentDb.Internal;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Implementation;

public sealed class Database : IDatabase
{
    private readonly IGitConnection _connection;
    private readonly IRecordSerializer _serializer;
    private readonly DatabaseOptions _options;
    private readonly string _refName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private DatabaseSnapshot _snapshot;

    internal Database(string name, IGitConnection connection, IRecordSerializer serializer, DatabaseOptions options)
    {
        Name = name;
        _connection = connection;
        _serializer = serializer;
        _options = options;
        _refName = BranchNaming.RefFor(name);
        _snapshot = EmptySnapshot();
    }

    public string Name { get; }
    public string CurrentCommit => Volatile.Read(ref _snapshot).CommitSha;
    public DateTimeOffset LastFetchedAt => Volatile.Read(ref _snapshot).FetchedAt;

    internal IGitConnection Connection => _connection;
    internal IRecordSerializer Serializer => _serializer;
    internal DatabaseOptions Options => _options;
    internal string RefName => _refName;
    internal SemaphoreSlim WriteLock => _writeLock;

    internal DatabaseSnapshot CurrentSnapshot => Volatile.Read(ref _snapshot);

    internal void SwapSnapshot(DatabaseSnapshot snapshot) =>
        Interlocked.Exchange(ref _snapshot, snapshot);

    public ITable<T> GetTable<T>(string name) where T : class
    {
        NameValidator.ThrowIfInvalid(name, nameof(name));
        return new Table<T>(this, name);
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(CancellationToken ct = default)
    {
        await EnsureOpenedAsync(ct);
        var snap = CurrentSnapshot;
        return snap.Tables.Keys.ToList();
    }

    public async Task<FetchResult> FetchAsync(CancellationToken ct = default)
    {
        var previous = CurrentSnapshot.CommitSha;
        var remoteSha = await _connection.ResolveRefAsync(_refName, ct);
        if (string.IsNullOrEmpty(remoteSha) || remoteSha == previous)
            return new FetchResult(false, previous, previous, Array.Empty<string>());

        var newSnap = await SnapshotBuilder.BuildAsync(_connection, remoteSha, ct);
        SwapSnapshot(newSnap);
        return new FetchResult(true, previous, remoteSha, Array.Empty<string>());
    }

    public IAsyncEnumerable<ChangeNotification> WatchAsync(CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in Task 16");

    internal async Task EnsureOpenedAsync(CancellationToken ct)
    {
        if (CurrentSnapshot.CommitSha.Length != 0) return;
        var remoteSha = await _connection.ResolveRefAsync(_refName, ct);
        if (string.IsNullOrEmpty(remoteSha)) return;
        var snap = await SnapshotBuilder.BuildAsync(_connection, remoteSha, ct);
        SwapSnapshot(snap);
    }

    private static DatabaseSnapshot EmptySnapshot() =>
        new("", DateTimeOffset.MinValue, FrozenDictionary<string, TableSnapshot>.Empty);
}
