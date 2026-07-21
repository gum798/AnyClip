using System.Security.Cryptography;
using System.Text;

namespace AnyClip.Core;

public static class Hashing
{
    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string text) =>
        Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// Echo-suppression key for a multi-file clip: sort per-file sha256 hex
    /// strings by ordinal, concatenate with no separator, sha256 the bytes.
    /// Order-independent. Keep in lockstep with Swift/Python.
    public static string AggregateFilesHash(IEnumerable<string> hashes) =>
        Sha256Hex(string.Concat(hashes.OrderBy(h => h, StringComparer.Ordinal)));
}
