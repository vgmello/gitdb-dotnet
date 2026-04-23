using System.Text;
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class TableReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, string Email, int Version);

    [Fact]
    public async Task Get_returns_null_when_record_absent()
    {
        var c = new InMemoryGitConnection();
        var db = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions())
            .GetDatabase("alpha");
        var table = db.GetTable<Account>("accounts");
        var result = await table.GetAsync("missing", null, Ct);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Get_returns_deserialized_record_and_version()
    {
        var c = new InMemoryGitConnection();
        // camelCase json since SystemTextJsonRecordSerializer uses JsonSerializerDefaults.Web
        var json = "{\"id\":\"a\",\"email\":\"x@y\",\"version\":1}"u8;
        var blob = await c.WriteBlobAsync(json.ToArray(), Ct);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, "tables/accounts/a.json", blob),
        }), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);
        await c.UpdateRefAsync("refs/heads/db/alpha", null, commit, Ct);

        var db = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions())
            .GetDatabase("alpha");
        var table = db.GetTable<Account>("accounts");

        var result = await table.GetAsync("a", null, Ct);
        result.Should().NotBeNull();
        result!.Record.Should().Be(new Account("a", "x@y", 1));
        result.Id.Should().Be("a");
        result.Version.Should().Be(blob);
        result.CommitSha.Should().Be(commit);
    }

    [Fact]
    public async Task Get_rejects_invalid_ids()
    {
        var c = new InMemoryGitConnection();
        var db = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions())
            .GetDatabase("alpha");
        var table = db.GetTable<Account>("accounts");
        var act = async () => await table.GetAsync("bad/id", null, Ct);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
