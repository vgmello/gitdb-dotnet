using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

[MemoryDiagnoser]
public class AllocationBenchmarks
{
    public sealed record Account(string Id, string Email);

    private ITable<Account>? _table;

    [GlobalSetup]
    public async Task Setup()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var db = doc.GetDatabase("alpha");
        _table = db.GetTable<Account>("accounts");
        await _table.PutAsync("a", new Account("a", "x@y"));
    }

    [Benchmark]
    public async Task<Versioned<Account>?> GetAsync_hot() => await _table!.GetAsync("a");
}
