namespace GitDocumentDb;

public interface IDocumentDatabase
{
    IDatabase GetDatabase(string name);
    Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default);
}
