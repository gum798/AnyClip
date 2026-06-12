using System.Text;

namespace AnyClip.Core;

/// Minimal DNS TXT codec (RFC 6763 §6): [len byte]["key=value"]. Entries
/// over 255 bytes are skipped; a zero-length entry ends the scan (we only
/// decode records we or zeroconf encoded). Port of the formacOS TXTCodec.
public static class TxtCodec
{
    public static byte[] Encode(IEnumerable<(string Key, string Value)> entries)
    {
        var output = new List<byte>();
        foreach (var (key, value) in entries)
        {
            var raw = Encoding.UTF8.GetBytes($"{key}={value}");
            if (raw.Length > 255) continue;
            output.Add((byte)raw.Length);
            output.AddRange(raw);
        }
        return output.ToArray();
    }

    public static Dictionary<string, string> Decode(byte[] data)
    {
        var result = new Dictionary<string, string>();
        int i = 0;
        while (i < data.Length)
        {
            int len = data[i];
            i += 1;
            if (len == 0 || i + len > data.Length) break;
            var s = Encoding.UTF8.GetString(data, i, len);
            int eq = s.IndexOf('=');
            if (eq > 0) result[s[..eq]] = s[(eq + 1)..];
            i += len;
        }
        return result;
    }
}
