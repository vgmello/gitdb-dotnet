using System.Text;
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class QueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record User(string Id, string Status, int Age);

    private static async Task<ITable<User>> NewTable(string schemaJson)
    {
        var c = new InMemoryGitConnection();
        var blob = await c.WriteBlobAsync(Encoding.UTF8.GetBytes(schemaJson), Ct);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, ".schema.json", blob),
        }), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);
        await c.UpdateRefAsync("refs/heads/db/alpha", null, commit, Ct);

        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        return doc.GetDatabase("alpha").GetTable<User>("users");
    }

    [Fact]
    public async Task Equality_index_returns_matching_records()
    {
        var t = await NewTable("""
          {"schemaVersion":1,"tables":{"users":{"indexes":[{"field":"status","type":"equality"}]}}}
          """);
        await t.PutAsync("a", new User("a", "active", 30), null, Ct);
        await t.PutAsync("b", new User("b", "active", 25), null, Ct);
        await t.PutAsync("c", new User("c", "inactive", 40), null, Ct);

        var q = Query.For<User>().Where(u => u.Status == "active").Build();
        var results = await t.QueryAsync(q, null, Ct);
        results.Select(r => r.Id).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task Range_index_returns_matching_records_in_order()
    {
        var t = await NewTable("""
          {"schemaVersion":1,"tables":{"users":{"indexes":[{"field":"age","type":"range"}]}}}
          """);
        await t.PutAsync("a", new User("a", "x", 20), null, Ct);
        await t.PutAsync("b", new User("b", "x", 30), null, Ct);
        await t.PutAsync("c", new User("c", "x", 40), null, Ct);

        var q = Query.For<User>().Where(u => u.Age >= 25).OrderBy(u => u.Age).Build();
        var results = await t.QueryAsync(q, null, Ct);
        results.Select(r => r.Id).Should().Equal("b", "c");
    }

    [Fact]
    public async Task Full_scan_allowed_under_threshold()
    {
        var t = await NewTable("""{"schemaVersion":1,"tables":{"users":{"indexes":[]}}}""");
        await t.PutAsync("a", new User("a", "x", 10), null, Ct);
        await t.PutAsync("b", new User("b", "y", 20), null, Ct);
        var q = Query.For<User>().Where(u => u.Age > 15).Build();
        var results = await t.QueryAsync(q, null, Ct);
        results.Select(r => r.Id).Should().Equal("b");
    }

    [Fact]
    public async Task Skip_take_applied()
    {
        var t = await NewTable("""{"schemaVersion":1,"tables":{"users":{"indexes":[]}}}""");
        for (var i = 0; i < 5; i++) await t.PutAsync($"u{i}", new User($"u{i}", "x", i), null, Ct);
        var q = Query.For<User>().OrderBy(u => u.Age).Skip(1).Take(2).Build();
        var results = await t.QueryAsync(q, null, Ct);
        results.Should().HaveCount(2);
        results.Select(r => r.Record.Age).Should().Equal(1, 2);
    }
}
