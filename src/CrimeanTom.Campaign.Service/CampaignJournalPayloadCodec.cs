using System.IO.Compression;

namespace CrimeanTom.Campaign.Service;

internal static class CampaignJournalPayloadCodec
{
    private static readonly byte[] BrotliMagic = [0x43, 0x54, 0x42, 0x31]; // CTB1
    private const int CompressionThresholdBytes = 1_024;
    private const int MaximumDecodedBytes = 128 * 1024 * 1024;

    public static byte[] Encode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < CompressionThresholdBytes)
        {
            return payload;
        }

        using var output = new MemoryStream(payload.Length / 4);
        output.Write(BrotliMagic);
        using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(payload);
        }

        var encoded = output.ToArray();
        return encoded.Length < payload.Length ? encoded : payload;
    }

    public static byte[] Decode(byte[] stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.Length < BrotliMagic.Length
            || !stored.AsSpan(0, BrotliMagic.Length).SequenceEqual(BrotliMagic))
        {
            return stored;
        }

        using var input = new MemoryStream(stored, BrotliMagic.Length, stored.Length - BrotliMagic.Length);
        using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Min(stored.Length * 4, MaximumDecodedBytes));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = decompressor.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > MaximumDecodedBytes)
            {
                throw new InvalidDataException("The compressed campaign journal payload exceeds the decoded size limit.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
