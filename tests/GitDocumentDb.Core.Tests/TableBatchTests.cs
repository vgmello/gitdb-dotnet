using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class TableBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, string Email);

    private static ITable<Account> NewTable()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        return doc.GetDatabase("alpha").GetTable<Account>("accounts");
    }

    [Fact]
    public async Task Batch_of_puts_produces_one_commit()
    {
        var t = NewTable();
        var ops = new[]
        {
            new WriteOperation<Account>(WriteOpKind.Put, "a", new Account("a", "x@y"), null),
            new WriteOperation<Account>(WriteOpKind.Put, "b", new Account("b", "y@z"), null),
            new WriteOperation<Account>(WriteOpKind.Put, "c", new Account("c", "z@w"), null),
        };
        var r = await t.CommitAsync(ops, null, Ct);
        r.Success.Should().BeTrue();
        r.Operations.Should().HaveCount(3).And.OnlyContain(o => o.Success);

        (await t.GetAsync("a", null, Ct)).Should().NotBeNull();
        (await t.GetAsync("b", null, Ct)).Should().NotBeNull();
        (await t.GetAsync("c", null, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_with_put_and_delete()
    {
        var t = NewTable();
        await t.PutAsync("a", new Account("a", "x@y"), null, Ct);

        var ops = new[]
        {
            new WriteOperation<Account>(WriteOpKind.Delete, "a", null, null),
            new WriteOperation<Account>(WriteOpKind.Put, "b", new Account("b", "y@z"), null),
        };
        var r = await t.CommitAsync(ops, null, Ct);
        r.Success.Should().BeTrue();
        (await t.GetAsync("a", null, Ct)).Should().BeNull();
        (await t.GetAsync("b", null, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Oversized_record_fails_batch_pre_push()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(),
            new DatabaseOptions { RecordSizeHardLimitBytes = 50 });
        var t = doc.GetDatabase("alpha").GetTable<Account>("accounts");
        var ops = new[]
        {
            new WriteOperation<Account>(WriteOpKind.Put, "a", new Account("a", "x@y"), null),
            new WriteOperation<Account>(WriteOpKind.Put, "b", new Account("b", new string('x', 1000)), null),
        };
        var r = await t.CommitAsync(ops, null, Ct);
        r.Success.Should().BeFalse();
        r.Operations.Should().Contain(o => o.Id == "b" && o.FailureReason == WriteFailureReason.RecordTooLarge);
        (await t.GetAsync("a", null, Ct)).Should().BeNull("atomic batch must not partially commit");
    }
}
