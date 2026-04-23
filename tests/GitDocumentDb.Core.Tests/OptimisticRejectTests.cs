using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class OptimisticRejectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, int Value);

    private static ITable<Account> NewTable()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        return doc.GetDatabase("alpha").GetTable<Account>("accounts");
    }

    [Fact]
    public async Task Reject_with_matching_version_succeeds()
    {
        var t = NewTable();
        var initial = await t.PutAsync("a", new Account("a", 1), null, Ct);
        var r = await t.PutAsync("a", new Account("a", 2),
            new WriteOptions { Mode = ConcurrencyMode.OptimisticReject, ExpectedVersion = initial.NewVersion },
            Ct);
        r.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Reject_with_stale_version_fails_with_conflict()
    {
        var t = NewTable();
        var initial = await t.PutAsync("a", new Account("a", 1), null, Ct);
        await t.PutAsync("a", new Account("a", 2), null, Ct);

        var r = await t.PutAsync("a", new Account("a", 3),
            new WriteOptions { Mode = ConcurrencyMode.OptimisticReject, ExpectedVersion = initial.NewVersion },
            Ct);
        r.Success.Should().BeFalse();
        r.Conflict.Should().NotBeNull();
        r.Conflict!.Reason.Should().Be(ConflictReason.VersionMismatch);
        r.Conflict.ExpectedVersion.Should().Be(initial.NewVersion);
        r.Conflict.ActualVersion.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reject_delete_of_missing_record_fails_with_ExpectedPresentButAbsent()
    {
        var t = NewTable();
        var r = await t.DeleteAsync("missing",
            new WriteOptions { Mode = ConcurrencyMode.OptimisticReject, ExpectedVersion = "some-sha" },
            Ct);
        r.Success.Should().BeFalse();
        r.Conflict.Should().NotBeNull();
        r.Conflict!.Reason.Should().Be(ConflictReason.ExpectedPresentButAbsent);
    }
}
