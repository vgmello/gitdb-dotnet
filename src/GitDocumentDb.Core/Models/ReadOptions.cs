namespace GitDocumentDb;

public sealed class ReadOptions
{
    public bool FetchFirst { get; init; }
    public TimeSpan? MaxStaleness { get; init; }
}
