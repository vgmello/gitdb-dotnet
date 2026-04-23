using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Merging;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class OptimisticMergeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record User(string Id, string Name, string Email, int Age);

    private static ITable<User> NewTable()
    {
        var c = new InMemoryGitConnection();
        var options = new DatabaseOptions
        {
            RecordMergers = new Dictionary<Type, object>
            {
                [typeof(User)] = new JsonPatchMerger<User>(),
            },
        };
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), options);
        return doc.GetDatabase("alpha").GetTable<User>("users");
    }

    [Fact]
    public async Task Non_overlapping_changes_merge_and_succeed()
    {
        var t = NewTable();
        var initial = await t.PutAsync("1", new User("1", "Alice", "a@x", 30), null, Ct);
        await t.PutAsync("1", new User("1", "Alice", "alice@new.com", 30), null, Ct);

        var r = await t.PutAsync("1",
            new User("1", "Alice L.", "a@x", 30),
            new WriteOptions
            {
                Mode = ConcurrencyMode.OptimisticMerge,
                ExpectedVersion = initial.NewVersion,
            }, Ct);

        r.Success.Should().BeTrue();
        var read = await t.GetAsync("1", null, Ct);
        read!.Record.Name.Should().Be("Alice L.");
        read.Record.Email.Should().Be("alice@new.com");
    }

    [Fact]
    public async Task Overlapping_changes_fail_with_unmergeable_conflict()
    {
        var t = NewTable();
        var initial = await t.PutAsync("1", new User("1", "Alice", "a@x", 30), null, Ct);
        await t.PutAsync("1", new User("1", "Alice", "remote@x", 30), null, Ct);

        var r = await t.PutAsync("1",
            new User("1", "Alice", "local@x", 30),
            new WriteOptions
            {
                Mode = ConcurrencyMode.OptimisticMerge,
                ExpectedVersion = initial.NewVersion,
            }, Ct);

        r.Success.Should().BeFalse();
        r.Conflict!.Reason.Should().Be(ConflictReason.UnmergeableChange);
    }

    [Fact]
    public async Task OptimisticMerge_without_registered_merger_falls_back_to_reject()
    {
        var c = new InMemoryGitConnection();
        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var t = doc.GetDatabase("alpha").GetTable<User>("users");

        var initial = await t.PutAsync("1", new User("1", "Alice", "a@x", 30), null, Ct);
        await t.PutAsync("1", new User("1", "Alice", "remote@x", 30), null, Ct);

        var r = await t.PutAsync("1",
            new User("1", "Alice", "local@x", 30),
            new WriteOptions
            {
                Mode = ConcurrencyMode.OptimisticMerge,
                ExpectedVersion = initial.NewVersion,
            }, Ct);

        r.Success.Should().BeFalse();
        r.Conflict!.Reason.Should().Be(ConflictReason.VersionMismatch);
    }
}
