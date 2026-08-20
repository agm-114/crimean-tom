using System.Text;

namespace CrimeanTom.Protocol;

public static class PipeFrameCodec
{
    private const int HeaderBytes = sizeof(int);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task WriteAsync(
        Stream stream,
        string value,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        ValidateMaximum(maximumFrameBytes);

        var payload = StrictUtf8.GetBytes(value);
        if (payload.Length == 0 || payload.Length > maximumFrameBytes)
        {
            throw new InvalidDataException(
                $"Frame length {payload.Length} is outside the permitted range 1..{maximumFrameBytes} bytes.");
        }

        var header = new byte[HeaderBytes];
        WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string?> ReadAsync(
        Stream stream,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        ValidateMaximum(maximumFrameBytes);

        var header = new byte[HeaderBytes];
        var headerBytes = await ReadExactlyAsync(
            stream,
            header,
            allowCleanEndOfStream: true,
            cancellationToken).ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return null;
        }

        var length = ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumFrameBytes)
        {
            throw new InvalidDataException(
                $"Frame length {length} is outside the permitted range 1..{maximumFrameBytes} bytes.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(
            stream,
            payload,
            allowCleanEndOfStream: false,
            cancellationToken).ConfigureAwait(false);
        return StrictUtf8.GetString(payload);
    }

    private static async Task<int> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        bool allowCleanEndOfStream,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer,
                offset,
                buffer.Length - offset,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowCleanEndOfStream && offset == 0)
                {
                    return 0;
                }

                throw new EndOfStreamException(
                    $"Stream ended after {offset} of {buffer.Length} required bytes.");
            }

            offset += read;
        }

        return offset;
    }

    private static int ReadInt32LittleEndian(byte[] bytes)
    {
        return bytes[0]
            | (bytes[1] << 8)
            | (bytes[2] << 16)
            | (bytes[3] << 24);
    }

    private static void WriteInt32LittleEndian(byte[] bytes, int value)
    {
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
        bytes[3] = (byte)(value >> 24);
    }

    private static void ValidateMaximum(int maximumFrameBytes)
    {
        if (maximumFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        }
    }
}