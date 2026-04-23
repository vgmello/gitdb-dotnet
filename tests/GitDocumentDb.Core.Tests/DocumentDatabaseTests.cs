using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class DocumentDatabaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DocumentDatabase Create(IGitConnection c) =>
        new(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());

    [Fact]
    public async Task ListDatabases_returns_empty_when_no_db_branches()
    {
        var c = new InMemoryGitConnection();
        var db = Create(c);
        (await db.ListDatabasesAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListDatabases_returns_branch_names_stripped_of_prefix()
    {
        var c = new InMemoryGitConnection();
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);
        await c.UpdateRefAsync("refs/heads/db/alpha", null, commit, Ct);
        await c.UpdateRefAsync("refs/heads/db/beta", null, commit, Ct);

        var db = Create(c);
        (await db.ListDatabasesAsync(Ct)).Should().BeEquivalentTo("alpha", "beta");
    }

    [Fact]
    public void GetDatabase_rejects_invalid_name()
    {
        var c = new InMemoryGitConnection();
        var db = Create(c);
        var act = () => db.GetDatabase("bad.name");
        act.Should().Throw<ArgumentException>();
    }
}
