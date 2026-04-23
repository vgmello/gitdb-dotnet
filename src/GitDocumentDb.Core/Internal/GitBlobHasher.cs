using System.Security.Cryptography;
using System.Text;

namespace GitDocumentDb.Internal;

internal static class GitBlobHasher
{
    public static string Hash(ReadOnlySpan<byte> content)
    {
        Span<byte> headerBuf = stackalloc byte[32];
        var headerLen = Encoding.ASCII.GetBytes($"blob {content.Length}\0", headerBuf);

        using var sha1 = SHA1.Create();
        sha1.TransformBlock(headerBuf[..headerLen].ToArray(), 0, headerLen, null, 0);
        var tail = content.ToArray();
        sha1.TransformFinalBlock(tail, 0, tail.Length);
        return Convert.ToHexStringLower(sha1.Hash!);
    }
}
