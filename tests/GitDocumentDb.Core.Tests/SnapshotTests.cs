using System.Text;
using FluentAssertions;
using GitDocumentDb.Internal;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class SnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Snapshot_built_from_tree_enumerates_tables_and_records()
    {
        var c = new InMemoryGitConnection();
        var b = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("{}"), Ct);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, "tables/accounts/a.json", b),
            new TreeMutation(TreeMutationKind.Upsert, "tables/accounts/b.json", b),
            new TreeMutation(TreeMutationKind.Upsert, "tables/orders/c.json",  b),
        }), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);

        var snap = await SnapshotBuilder.BuildAsync(c, commit, Ct);

        snap.CommitSha.Should().Be(commit);
        snap.Tables.Keys.Should().BeEquivalentTo("accounts", "orders");
        snap.Tables["accounts"].Records.Keys.Should().BeEquivalentTo("a", "b");
        snap.Tables["orders"].Records.Keys.Should().BeEquivalentTo("c");
        snap.Tables["accounts"].Records["a"].Should().Be(b);
    }

    [Fact]
    public async Task Empty_tree_produces_empty_snapshot()
    {
        var c = new InMemoryGitConnection();
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);
        var snap = await SnapshotBuilder.BuildAsync(c, commit, Ct);
        snap.Tables.Should().BeEmpty();
    }
}
