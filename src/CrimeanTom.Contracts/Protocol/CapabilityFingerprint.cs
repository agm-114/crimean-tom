using System.Security.Cryptography;
using System.Text;

namespace CrimeanTom.Protocol;

public static class CapabilityFingerprint
{
    public static string ComputeSha256(
        IEnumerable<KeyValuePair<string, string?>> fields)
    {
        if (fields is null)
        {
            throw new ArgumentNullException(nameof(fields));
        }

        var ordered = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field.Key))
            {
                throw new ArgumentException("Fingerprint field names cannot be empty.", nameof(fields));
            }

            if (!ordered.TryAdd(field.Key, field.Value))
            {
                throw new ArgumentException(
                    $"Fingerprint field '{field.Key}' was supplied more than once.",
                    nameof(fields));
            }
        }

        using var canonical = new MemoryStream();
        foreach (var field in ordered)
        {
            WriteValue(canonical, field.Key);
            WriteValue(canonical, field.Value);
        }

        canonical.Position = 0;
        using var sha256 = SHA256.Create();
        return ToLowerHex(sha256.ComputeHash(canonical));
    }

    private static void WriteValue(Stream stream, string? value)
    {
        if (value is null)
        {
            WriteInt32LittleEndian(stream, -1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32LittleEndian(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteInt32LittleEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var result = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }
}
