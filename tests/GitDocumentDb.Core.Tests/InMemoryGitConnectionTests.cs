using System.Text;
using FluentAssertions;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;

namespace GitDocumentDb.Tests;

public class InMemoryGitConnectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WriteBlob_returns_deterministic_sha()
    {
        var c = new InMemoryGitConnection();
        var sha1 = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("hello"), Ct);
        var sha2 = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("hello"), Ct);
        sha1.Should().Be(sha2);
        sha1.Should().Be("b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0");
    }

    [Fact]
    public async Task GetBlob_returns_content_written()
    {
        var c = new InMemoryGitConnection();
        var bytes = Encoding.UTF8.GetBytes("payload");
        var sha = await c.WriteBlobAsync(bytes, Ct);
        var got = await c.GetBlobAsync(sha, Ct);
        got.ToArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task Tree_roundtrip_enumerates_children()
    {
        var c = new InMemoryGitConnection();
        var b1 = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("one"), Ct);
        var b2 = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("two"), Ct);

        var treeSha = await c.WriteTreeAsync(new TreeBuildSpec(
            BaseTreeSha: null,
            Mutations: new[]
            {
                new TreeMutation(TreeMutationKind.Upsert, "tables/a/1.json", b1),
                new TreeMutation(TreeMutationKind.Upsert, "tables/a/2.json", b2),
            }), Ct);

        var commitSha = await c.CreateCommitAsync(new CommitSpec(
            treeSha, null, "t", "t@x", DateTimeOffset.UnixEpoch, "init"), Ct);

        var tree = await c.GetTreeAsync(commitSha, Ct);
        tree.TryGetBlob("tables/a/1.json", out var s1).Should().BeTrue();
        s1.Should().Be(b1);
        tree.EnumerateChildren("tables/a").Select(e => e.Name)
            .Should().BeEquivalentTo("1.json", "2.json");
    }

    [Fact]
    public async Task UpdateRef_succeeds_on_matching_expected()
    {
        var c = new InMemoryGitConnection();
        var commit = await CreateEmptyCommit(c, null);
        var result = await c.UpdateRefAsync("refs/heads/db/x", null, commit, Ct);
        result.Success.Should().BeTrue();
        (await c.ResolveRefAsync("refs/heads/db/x", Ct)).Should().Be(commit);
    }

    [Fact]
    public async Task UpdateRef_fails_on_non_fast_forward()
    {
        var c = new InMemoryGitConnection();
        var c1 = await CreateEmptyCommit(c, null);
        await c.UpdateRefAsync("refs/heads/db/x", null, c1, Ct);

        var c2 = await CreateEmptyCommit(c, c1);
        var bad = await c.UpdateRefAsync("refs/heads/db/x", null, c2, Ct);
        bad.Success.Should().BeFalse();
        bad.Reason.Should().Be(PushRejectReason.NonFastForward);
    }

    [Fact]
    public async Task Delete_mutation_removes_entry_from_new_tree()
    {
        var c = new InMemoryGitConnection();
        var b = await c.WriteBlobAsync(Encoding.UTF8.GetBytes("x"), Ct);
        var t1 = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, "a/1.json", b),
            new TreeMutation(TreeMutationKind.Upsert, "a/2.json", b),
        }), Ct);
        var t2 = await c.WriteTreeAsync(new TreeBuildSpec(t1, new[]
        {
            new TreeMutation(TreeMutationKind.Delete, "a/1.json", null),
        }), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(
            t2, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);
        var tree = await c.GetTreeAsync(commit, Ct);
        tree.TryGetBlob("a/1.json", out _).Should().BeFalse();
        tree.TryGetBlob("a/2.json", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ListRefs_filters_by_prefix()
    {
        var c = new InMemoryGitConnection();
        var commit = await CreateEmptyCommit(c, null);
        await c.UpdateRefAsync("refs/heads/db/one", null, commit, Ct);
        await c.UpdateRefAsync("refs/heads/db/two", null, commit, Ct);
        await c.UpdateRefAsync("refs/heads/main", null, commit, Ct);
        var dbs = await c.ListRefsAsync("refs/heads/db/", Ct);
        dbs.Should().BeEquivalentTo("refs/heads/db/one", "refs/heads/db/two");
    }

    private static async Task<string> CreateEmptyCommit(IGitConnection c, string? parent)
    {
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), Ct);
        return await c.CreateCommitAsync(new CommitSpec(
            tree, parent, "t", "t@x", DateTimeOffset.UnixEpoch, "m"), Ct);
    }
}
