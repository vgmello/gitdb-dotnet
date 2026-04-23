using GitDocumentDb.Internal;
using GitDocumentDb.Transport;

namespace GitDocumentDb.Implementation;

public sealed class DocumentDatabase : IDocumentDatabase
{
    private readonly IGitConnection _connection;
    private readonly IRecordSerializer _serializer;
    private readonly DatabaseOptions _options;
    private readonly Dictionary<string, Database> _databases = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public DocumentDatabase(
        IGitConnection connection,
        IRecordSerializer serializer,
        DatabaseOptions options)
    {
        _connection = connection;
        _serializer = serializer;
        _options = options;
    }

    public IDatabase GetDatabase(string name)
    {
        NameValidator.ThrowIfInvalid(name, nameof(name));
        lock (_sync)
        {
            if (!_databases.TryGetValue(name, out var db))
            {
                db = new Database(name, _connection, _serializer, _options);
                _databases[name] = db;
            }
            return db;
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken ct = default)
    {
        var refs = await _connection.ListRefsAsync(BranchNaming.DatabaseRefPrefix, ct);
        return refs.Select(BranchNaming.NameFrom).ToList();
    }
}
