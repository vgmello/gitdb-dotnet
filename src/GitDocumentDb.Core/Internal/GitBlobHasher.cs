using System.Security.Cryptography;
using System.Text;

namespace GitDocumentDb.Internal;

internal static class GitBlobHasher
{
    public static string Hash(ReadOnlySpan<byte> content)
    {
        Span<byte> headerBuf = stackalloc byte[32];
        var headerLen = Encoding.ASCII.GetBytes($"blob {content.Length}\0", headerBuf);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(headerBuf[..headerLen]);
        hash.AppendData(content);

        Span<byte> digest = stackalloc byte[20];
        hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest);
    }
}
