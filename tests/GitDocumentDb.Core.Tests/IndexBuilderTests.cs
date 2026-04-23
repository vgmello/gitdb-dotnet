using System.Text.Json;
using FluentAssertions;
using GitDocumentDb.Indexing;
using GitDocumentDb.Schema;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class IndexBuilderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyDictionary<string, string>> Seed(IGitConnection c, Dictionary<string, object> records)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, rec) in records)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(rec, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            map[id] = await c.WriteBlobAsync(bytes, Ct);
        }
        return map;
    }

    [Fact]
    public async Task Builds_equality_index()
    {
        var c = new InMemoryGitConnection();
        var records = await Seed(c, new() {
            ["a"] = new { id = "a", status = "active" },
            ["b"] = new { id = "b", status = "active" },
            ["c"] = new { id = "c", status = "inactive" },
        });
        var schema = new TableSchema("t", new[] { new IndexDefinition("status", IndexType.Equality, false) });
        var result = await IndexBuilder.BuildAsync(c, schema, records, Ct);
        result.UniqueViolation.Should().BeNull();
        var idx = result.Indexes["status"].Should().BeOfType<EqualityIndex>().Subject;
        idx.ByValue.Should().HaveCount(2);
    }

    [Fact]
    public async Task Builds_unique_equality_index_and_detects_violation()
    {
        var c = new InMemoryGitConnection();
        var records = await Seed(c, new() {
            ["a"] = new { id = "a", email = "x@y" },
            ["b"] = new { id = "b", email = "x@y" },
        });
        var schema = new TableSchema("t", new[] { new IndexDefinition("email", IndexType.Equality, true) });
        var result = await IndexBuilder.BuildAsync(c, schema, records, Ct);
        result.UniqueViolation.Should().NotBeNull();
    }

    [Fact]
    public async Task Builds_range_index()
    {
        var c = new InMemoryGitConnection();
        var records = await Seed(c, new() {
            ["a"] = new { id = "a", age = 30 },
            ["b"] = new { id = "b", age = 25 },
            ["c"] = new { id = "c", age = 40 },
        });
        var schema = new TableSchema("t", new[] { new IndexDefinition("age", IndexType.Range, false) });
        var result = await IndexBuilder.BuildAsync(c, schema, records, Ct);
        var idx = result.Indexes["age"].Should().BeOfType<RangeIndex>().Subject;
        idx.Sorted.Keys.Should().Equal(new object[] { (long)25, (long)30, (long)40 });
    }
}
