using System.Text;
using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class UniqueIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, string Email);

    private static async Task<ITable<Account>> NewTableWithUniqueEmail()
    {
        var c = new InMemoryGitConnection();
        var schema = """
          {"schemaVersion":1,"tables":{"accounts":{"indexes":[{"field":"email","type":"equality","unique":true}]}}}
          """;
        var blob = await c.WriteBlobAsync(Encoding.UTF8.GetBytes(schema), Ct);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, ".schema.json", blob),
        }), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);
        await c.UpdateRefAsync("refs/heads/db/alpha", null, commit, Ct);

        var doc = new DocumentDatabase(c, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        return doc.GetDatabase("alpha").GetTable<Account>("accounts");
    }

    [Fact]
    public async Task Duplicate_unique_field_is_rejected()
    {
        var t = await NewTableWithUniqueEmail();
        var first = await t.PutAsync("a", new Account("a", "x@y"), null, Ct);
        first.Success.Should().BeTrue();

        var second = await t.PutAsync("b", new Account("b", "x@y"), null, Ct);
        second.Success.Should().BeFalse();
        second.Conflict!.Reason.Should().Be(ConflictReason.UniqueViolation);
    }

    [Fact]
    public async Task Same_record_reupdate_is_not_a_violation()
    {
        var t = await NewTableWithUniqueEmail();
        await t.PutAsync("a", new Account("a", "x@y"), null, Ct);
        var again = await t.PutAsync("a", new Account("a", "x@y"), null, Ct);
        again.Success.Should().BeTrue();
    }
}
