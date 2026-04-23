using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class ChangeNotificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, int V);

    [Fact]
    public async Task Watch_yields_notification_when_fetch_observes_new_commit()
    {
        var backend = new InMemoryGitConnection();
        var doc1 = new DocumentDatabase(backend, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var doc2 = new DocumentDatabase(backend, new SystemTextJsonRecordSerializer(), new DatabaseOptions());

        var db1 = doc1.GetDatabase("alpha");
        var db2 = doc2.GetDatabase("alpha");
        var t2 = db2.GetTable<Account>("accounts");

        using var testCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        testCts.CancelAfter(TimeSpan.FromSeconds(5));

        var received = new List<ChangeNotification>();
        var watchTask = Task.Run(async () =>
        {
            await foreach (var n in db1.WatchAsync(testCts.Token))
            {
                received.Add(n);
                if (received.Count == 1) break;
            }
        }, testCts.Token);

        // Give the watch loop a moment to attach.
        await Task.Delay(50, Ct);

        // Another writer makes a change.
        await t2.PutAsync("a", new Account("a", 1), null, Ct);

        // Explicit fetch on db1 to trigger the notification.
        await db1.FetchAsync(Ct);

        await watchTask;
        received.Should().HaveCount(1);
        received[0].Reason.Should().Be(ChangeReason.RemoteAdvance);
        received[0].CommitSha.Should().Be(db1.CurrentCommit);
    }
}
