namespace Hilke.Ant.Protocol;

/// <summary>A decoded ANT message: its id and the raw DATA payload (excluding sync/length/checksum).</summary>
internal readonly struct AntMessage
{
    public AntMessage(AntMessageId id, ReadOnlyMemory<byte> payload)
    {
        Id = id;
        Payload = payload;
    }

    public AntMessageId Id { get; }

    /// <summary>DATA bytes. For channel messages Payload[0] is the channel number.</summary>
    public ReadOnlyMemory<byte> Payload { get; }
}

/// <summary>Encoding + checksum helpers for the ANT frame: SYNC | LENGTH | MSG_ID | DATA | CHECKSUM.</summary>
internal static class AntFrame
{
    /// <summary>Overhead bytes around DATA: SYNC + LENGTH + MSG_ID + CHECKSUM.</summary>
    public const int Overhead = 4;

    /// <summary>XOR of every byte from SYNC through the last DATA byte.</summary>
    public static byte Checksum(ReadOnlySpan<byte> frameWithoutChecksum)
    {
        byte c = 0;
        foreach (byte b in frameWithoutChecksum)
            c ^= b;
        return c;
    }

    /// <summary>Encode a frame into <paramref name="destination"/>. Returns total bytes written.</summary>
    public static int Encode(AntMessageId id, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int total = Overhead + data.Length;
        if (destination.Length < total)
            throw new ArgumentException($"Destination too small: need {total}, have {destination.Length}.", nameof(destination));
        if (data.Length > byte.MaxValue)
            throw new ArgumentException($"Data too long: {data.Length} > {byte.MaxValue}.", nameof(data));

        destination[0] = AntConstants.Sync;
        destination[1] = (byte)data.Length;
        destination[2] = (byte)id;
        data.CopyTo(destination[3..]);
        destination[3 + data.Length] = Checksum(destination[..(3 + data.Length)]);
        return total;
    }

    /// <summary>Encode a frame into a freshly allocated array.</summary>
    public static byte[] Encode(AntMessageId id, ReadOnlySpan<byte> data)
    {
        var buffer = new byte[Overhead + data.Length];
        Encode(id, data, buffer);
        return buffer;
    }
}
