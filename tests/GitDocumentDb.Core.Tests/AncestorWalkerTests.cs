using FluentAssertions;
using GitDocumentDb.Internal;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class AncestorWalkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> EmptyCommit(IGitConnection c, string? parent, string message = "m")
    {
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), Ct);
        return await c.CreateCommitAsync(new CommitSpec(
            tree, parent, "t", "t@x", DateTimeOffset.UnixEpoch, message), Ct);
    }

    [Fact]
    public async Task Commit_is_ancestor_of_itself()
    {
        var c = new InMemoryGitConnection();
        var a = await EmptyCommit(c, null);
        (await AncestorWalker.IsAncestorAsync(c, a, a, 1000, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Parent_is_ancestor_of_child()
    {
        var c = new InMemoryGitConnection();
        var a = await EmptyCommit(c, null);
        var b = await EmptyCommit(c, a);
        (await AncestorWalker.IsAncestorAsync(c, a, b, 1000, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Unrelated_commits_are_not_ancestors()
    {
        var c = new InMemoryGitConnection();
        var a = await EmptyCommit(c, null, "a");
        var b = await EmptyCommit(c, null, "b");
        (await AncestorWalker.IsAncestorAsync(c, a, b, 1000, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Walk_stops_at_max_depth()
    {
        var c = new InMemoryGitConnection();
        var chain = await EmptyCommit(c, null);
        for (var i = 0; i < 10; i++) chain = await EmptyCommit(c, chain);
        var root = await EmptyCommit(c, null);
        (await AncestorWalker.IsAncestorAsync(c, root, chain, 3, Ct)).Should().BeFalse();
    }
}
