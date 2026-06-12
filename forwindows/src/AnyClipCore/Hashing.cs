using System.Security.Cryptography;
using System.Text;

namespace AnyClip.Core;

public static class Hashing
{
    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string text) =>
        Sha256Hex(Encoding.UTF8.GetBytes(text));
}
