using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class CreateIfAbsentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, int V);

    private static ITable<Account> NewTable()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        return doc.GetDatabase("alpha").GetTable<Account>("accounts");
    }

    [Fact]
    public async Task Create_if_absent_succeeds_when_absent()
    {
        var t = NewTable();
        var r = await t.PutAsync("a", new Account("a", 1),
            new WriteOptions { Mode = ConcurrencyMode.OptimisticReject, ExpectedVersion = Versions.Absent }, Ct);
        r.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Create_if_absent_fails_when_present()
    {
        var t = NewTable();
        await t.PutAsync("a", new Account("a", 1), null, Ct);
        var r = await t.PutAsync("a", new Account("a", 2),
            new WriteOptions { Mode = ConcurrencyMode.OptimisticReject, ExpectedVersion = Versions.Absent }, Ct);
        r.Success.Should().BeFalse();
        r.Conflict!.Reason.Should().Be(ConflictReason.ExpectedAbsentButPresent);
    }

    [Fact]
    public async Task LastWriteWins_ignores_expected_version()
    {
        var t = NewTable();
        await t.PutAsync("a", new Account("a", 1), null, Ct);
        var r = await t.PutAsync("a", new Account("a", 2),
            new WriteOptions { Mode = ConcurrencyMode.LastWriteWins, ExpectedVersion = "some-stale-sha" }, Ct);
        r.Success.Should().BeTrue();
    }
}
