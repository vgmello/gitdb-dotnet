namespace GitDocumentDb;

public sealed class WriteOptions
{
    public ConcurrencyMode Mode { get; init; } = ConcurrencyMode.LastWriteWins;
    public string? ExpectedVersion { get; init; }
    public int MaxRetries { get; init; } = 3;
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan MaxRetryBackoff { get; init; } = TimeSpan.FromSeconds(5);
    public string? Author { get; init; }
    public string? CommitMessage { get; init; }
}
