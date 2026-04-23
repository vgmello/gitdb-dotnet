using FluentAssertions;
using GitDocumentDb.Implementation;
using GitDocumentDb.Serialization;
using GitDocumentDb.Transport.InMemory;
using Xunit;

namespace GitDocumentDb.Tests;

public class MultiWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Account(string Id, int Value);

    [Fact]
    public async Task Two_writers_interleaving_all_land_under_last_write_wins()
    {
        var backend = new InMemoryGitConnection();
        var doc1 = new DocumentDatabase(backend, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var doc2 = new DocumentDatabase(backend, new SystemTextJsonRecordSerializer(), new DatabaseOptions());
        var t1 = doc1.GetDatabase("alpha").GetTable<Account>("accounts");
        var t2 = doc2.GetDatabase("alpha").GetTable<Account>("accounts");

        var writes1 = Enumerable.Range(0, 25)
            .Select(i => t1.PutAsync($"w1-{i}", new Account($"w1-{i}", i), null, Ct))
            .ToArray();
        var writes2 = Enumerable.Range(0, 25)
            .Select(i => t2.PutAsync($"w2-{i}", new Account($"w2-{i}", i), null, Ct))
            .ToArray();

        var allResults = await Task.WhenAll(writes1.Concat(writes2));
        allResults.Should().OnlyContain(r => r.Success);

        await doc1.GetDatabase("alpha").FetchAsync(Ct);
        await doc2.GetDatabase("alpha").FetchAsync(Ct);
        for (var i = 0; i < 25; i++)
        {
            (await t1.GetAsync($"w1-{i}", null, Ct)).Should().NotBeNull();
            (await t1.GetAsync($"w2-{i}", null, Ct)).Should().NotBeNull();
            (await t2.GetAsync($"w1-{i}", null, Ct)).Should().NotBeNull();
            (await t2.GetAsync($"w2-{i}", null, Ct)).Should().NotBeNull();
        }
    }
}
