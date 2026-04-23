using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class TableWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, string Email, int Version);

    private static (DocumentDatabase doc, IDatabase db, ITable<Account> t) NewTable()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var db = doc.GetDatabase("alpha");
        return (doc, db, db.GetTable<Account>("accounts"));
    }

    [Fact]
    public async Task Put_into_empty_database_creates_record_and_advances_commit()
    {
        var (_, db, t) = NewTable();
        var result = await t.PutAsync("a", new Account("a", "x@y", 1), null, Ct);
        result.Success.Should().BeTrue();
        result.NewVersion.Should().NotBeNullOrEmpty();
        result.NewCommitSha.Should().NotBeNullOrEmpty();

        db.CurrentCommit.Should().Be(result.NewCommitSha);

        var read = await t.GetAsync("a", null, Ct);
        read.Should().NotBeNull();
        read!.Record.Should().Be(new Account("a", "x@y", 1));
        read.Version.Should().Be(result.NewVersion);
    }

    [Fact]
    public async Task Put_twice_updates_the_record()
    {
        var (_, _, t) = NewTable();
        await t.PutAsync("a", new Account("a", "x@y", 1), null, Ct);
        var r = await t.PutAsync("a", new Account("a", "x@y", 2), null, Ct);
        r.Success.Should().BeTrue();
        var read = await t.GetAsync("a", null, Ct);
        read!.Record.Version.Should().Be(2);
    }

    [Fact]
    public async Task Delete_removes_the_record()
    {
        var (_, _, t) = NewTable();
        await t.PutAsync("a", new Account("a", "x@y", 1), null, Ct);
        var r = await t.DeleteAsync("a", null, Ct);
        r.Success.Should().BeTrue();
        (await t.GetAsync("a", null, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_of_missing_record_under_last_write_wins_succeeds_noop()
    {
        var (_, _, t) = NewTable();
        var r = await t.DeleteAsync("missing", null, Ct);
        r.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Record_over_hard_size_limit_fails()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(),
            new DatabaseOptions { RecordSizeHardLimitBytes = 50 });
        var table = doc.GetDatabase("alpha").GetTable<Account>("accounts");
        var big = new Account("a", new string('x', 1000), 1);
        var r = await table.PutAsync("a", big, null, Ct);
        r.Success.Should().BeFalse();
        r.FailureReason.Should().Be(WriteFailureReason.RecordTooLarge);
    }
}
