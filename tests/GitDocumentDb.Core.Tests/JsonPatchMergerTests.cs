using FluentAssertions;
using GitDocumentDb.Merging;

namespace GitDocumentDb.Tests;

public class JsonPatchMergerTests
{
    public sealed record User(string Id, string Name, string Email, int Age);

    [Fact]
    public void Non_overlapping_changes_merge_successfully()
    {
        var merger = new JsonPatchMerger<User>();
        var baseRec = new User("1", "Alice", "a@x", 30);
        var local = baseRec with { Name = "Alice L." };
        var remote = baseRec with { Email = "alice@new.com" };

        var result = merger.Merge(baseRec, local, remote);
        result.Succeeded.Should().BeTrue();
        result.Merged!.Name.Should().Be("Alice L.");
        result.Merged.Email.Should().Be("alice@new.com");
        result.Merged.Id.Should().Be("1");
        result.Merged.Age.Should().Be(30);
    }

    [Fact]
    public void Overlapping_changes_fail_to_merge()
    {
        var merger = new JsonPatchMerger<User>();
        var baseRec = new User("1", "Alice", "a@x", 30);
        var local = baseRec with { Email = "local@x" };
        var remote = baseRec with { Email = "remote@x" };

        var result = merger.Merge(baseRec, local, remote);
        result.Succeeded.Should().BeFalse();
        result.ConflictDescription.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Unchanged_local_returns_remote()
    {
        var merger = new JsonPatchMerger<User>();
        var baseRec = new User("1", "Alice", "a@x", 30);
        var remote = baseRec with { Email = "alice@new.com" };
        var result = merger.Merge(baseRec, baseRec, remote);
        result.Succeeded.Should().BeTrue();
        result.Merged!.Email.Should().Be("alice@new.com");
    }

    [Fact]
    public void Unchanged_remote_returns_local()
    {
        var merger = new JsonPatchMerger<User>();
        var baseRec = new User("1", "Alice", "a@x", 30);
        var local = baseRec with { Name = "Alice L." };
        var result = merger.Merge(baseRec, local, baseRec);
        result.Succeeded.Should().BeTrue();
        result.Merged!.Name.Should().Be("Alice L.");
    }
}
