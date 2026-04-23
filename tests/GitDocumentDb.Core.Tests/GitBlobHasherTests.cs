using System.Text;
using FluentAssertions;
using GitDocumentDb.Internal;

namespace GitDocumentDb.Tests;

public class GitBlobHasherTests
{
    [Fact]
    public void Empty_blob_sha_matches_git()
    {
        GitBlobHasher.Hash(ReadOnlySpan<byte>.Empty)
            .Should().Be("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391");
    }

    [Fact]
    public void Hello_blob_sha_matches_git()
    {
        GitBlobHasher.Hash(Encoding.UTF8.GetBytes("hello"))
            .Should().Be("b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0");
    }
}
