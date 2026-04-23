using System.Text;
using FluentAssertions;
using GitDocumentDb.Schema;
using GitDocumentDb.Transport;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class SchemaLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_schema_returns_empty()
    {
        var c = new InMemoryGitConnection();
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, Array.Empty<TreeMutation>()), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);
        var schema = await SchemaLoader.LoadAsync(c, commit, Ct);
        schema.Tables.Should().BeEmpty();
    }

    [Fact]
    public async Task Loads_equality_and_range_indexes_with_unique_flag()
    {
        var json = """
        {
          "schemaVersion": 1,
          "tables": {
            "accounts": {
              "indexes": [
                { "field": "email", "type": "equality", "unique": true },
                { "field": "createdAt", "type": "range" }
              ]
            }
          }
        }
        """;
        var c = new InMemoryGitConnection();
        var blob = await c.WriteBlobAsync(Encoding.UTF8.GetBytes(json), Ct);
        var tree = await c.WriteTreeAsync(new TreeBuildSpec(null, new[]
        {
            new TreeMutation(TreeMutationKind.Upsert, ".schema.json", blob),
        }), Ct);
        var commit = await c.CreateCommitAsync(new CommitSpec(tree, null, "t", "t@x", DateTimeOffset.UnixEpoch, ""), Ct);

        var schema = await SchemaLoader.LoadAsync(c, commit, Ct);
        schema.Tables.Should().ContainKey("accounts");
        var defs = schema.Tables["accounts"].Indexes;
        defs.Should().HaveCount(2);
        defs.Should().Contain(d => d.Field == "email" && d.Type == IndexType.Equality && d.Unique);
        defs.Should().Contain(d => d.Field == "createdAt" && d.Type == IndexType.Range && !d.Unique);
    }
}
