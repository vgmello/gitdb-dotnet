using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class ForcePushTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Doc(string Id, int V);

    [Fact]
    public async Task Non_ancestor_remote_triggers_history_rewritten_notification()
    {
        var backend = new InMemoryGitConnection();
        var doc1 = new DocumentDatabase(backend, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var db1 = doc1.GetDatabase("alpha");
        var t1 = db1.GetTable<Doc>("things");

        await t1.PutAsync("a", new Doc("a", 1), null, Ct);
        var firstCommit = db1.CurrentCommit;

        // Externally force-push an unrelated branch head (non-descendant)
        var tree = await backend.WriteTreeAsync(
            new GitDocumentDb.Transport.TreeBuildSpec(null, Array.Empty<GitDocumentDb.Transport.TreeMutation>()), Ct);
        var rogue = await backend.CreateCommitAsync(
            new GitDocumentDb.Transport.CommitSpec(
                tree, null, "rogue", "r@x", DateTimeOffset.UnixEpoch, "forced"), Ct);

        // Simulate force-push: UpdateRefAsync with the current SHA as "expected", pointing to unrelated commit
        await backend.UpdateRefAsync("refs/heads/db/alpha", firstCommit, rogue, Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var received = new List<ChangeNotification>();
        var watch = Task.Run(async () =>
        {
            await foreach (var n in db1.WatchAsync(cts.Token))
            {
                received.Add(n);
                if (received.Count == 1) break;
            }
        }, cts.Token);

        await Task.Delay(50, Ct);
        var fetch = await db1.FetchAsync(Ct);

        await watch;
        received.Should().HaveCount(1);
        received[0].Reason.Should().Be(ChangeReason.HistoryRewritten);
        fetch.HadChanges.Should().BeTrue();
    }
}
