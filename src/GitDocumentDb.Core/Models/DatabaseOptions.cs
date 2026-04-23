namespace GitDocumentDb;

public sealed class DatabaseOptions
{
    public ConcurrencyMode DefaultConcurrencyMode { get; init; } = ConcurrencyMode.LastWriteWins;
    public bool EnableBackgroundFetch { get; init; }
    public TimeSpan BackgroundFetchInterval { get; init; } = TimeSpan.FromSeconds(60);
    public long RecordSizeSoftLimitBytes { get; init; } = 1L * 1024 * 1024;
    public long RecordSizeHardLimitBytes { get; init; } = 10L * 1024 * 1024;
    public int MaxSnapshotCacheCount { get; init; } = 2;
    public string DefaultAuthorName { get; init; } = "GitDocumentDb";
    public string DefaultAuthorEmail { get; init; } = "gitdb@localhost";
}
