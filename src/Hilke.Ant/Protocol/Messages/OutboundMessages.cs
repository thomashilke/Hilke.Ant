using Hilke.Ant.Model;

namespace Hilke.Ant.Protocol.Messages;

/// <summary>Pure encoders producing complete frame bytes for outbound (host → device) messages.</summary>
internal static class OutboundMessages
{
    public static byte[] AssignChannel(byte channel, ChannelType type, byte network)
        => AntFrame.Encode(AntMessageId.AssignChannel, stackalloc byte[] { channel, (byte)type, network });

    public static byte[] SetChannelId(byte channel, ChannelId id)
        => AntFrame.Encode(AntMessageId.ChannelId, stackalloc byte[]
        {
            channel,
            (byte)(id.DeviceNumber & 0xFF),
            (byte)(id.DeviceNumber >> 8),
            id.DeviceType,
            id.TransmissionType,
        });

    public static byte[] SetRfFrequency(byte channel, byte frequency)
        => AntFrame.Encode(AntMessageId.RfFrequency, stackalloc byte[] { channel, frequency });

    public static byte[] SetChannelPeriod(byte channel, ushort period)
        => AntFrame.Encode(AntMessageId.ChannelPeriod, stackalloc byte[]
        {
            channel,
            (byte)(period & 0xFF),
            (byte)(period >> 8),
        });

    public static byte[] SetSearchTimeout(byte channel, byte count)
        => AntFrame.Encode(AntMessageId.SearchTimeout, stackalloc byte[] { channel, count });

    public static byte[] SetNetworkKey(byte network, ReadOnlySpan<byte> key)
    {
        if (key.Length != 8)
            throw new ArgumentException("Network key must be exactly 8 bytes.", nameof(key));
        Span<byte> data = stackalloc byte[9];
        data[0] = network;
        key.CopyTo(data[1..]);
        return AntFrame.Encode(AntMessageId.NetworkKey, data);
    }

    public static byte[] SetChannelTxPower(byte channel, sbyte level)
        => AntFrame.Encode(AntMessageId.ChannelTransmitPower, stackalloc byte[] { channel, (byte)level });

    public static byte[] OpenChannel(byte channel)
        => AntFrame.Encode(AntMessageId.OpenChannel, stackalloc byte[] { channel });

    public static byte[] CloseChannel(byte channel)
        => AntFrame.Encode(AntMessageId.CloseChannel, stackalloc byte[] { channel });

    public static byte[] UnassignChannel(byte channel)
        => AntFrame.Encode(AntMessageId.UnassignChannel, stackalloc byte[] { channel });

    public static byte[] OpenRxScanMode()
        => AntFrame.Encode(AntMessageId.OpenRxScanMode, stackalloc byte[] { 0 });

    public static byte[] ResetSystem()
        => AntFrame.Encode(AntMessageId.ResetSystem, stackalloc byte[] { 0 });

    public static byte[] RequestMessage(byte channel, AntMessageId requestedId)
        => AntFrame.Encode(AntMessageId.RequestMessage, stackalloc byte[] { channel, (byte)requestedId });

    public static byte[] LibConfig(byte flags)
        => AntFrame.Encode(AntMessageId.LibConfig, stackalloc byte[] { 0, flags });

    public static byte[] EnableExtRxMessages(bool enable)
        => AntFrame.Encode(AntMessageId.EnableExtRxMessages, stackalloc byte[] { 0, (byte)(enable ? 1 : 0) });

    public static byte[] BroadcastData(byte channel, ReadOnlySpan<byte> page8)
        => EncodeData(AntMessageId.BroadcastData, channel, page8);

    public static byte[] AcknowledgedData(byte channel, ReadOnlySpan<byte> page8)
        => EncodeData(AntMessageId.AcknowledgedData, channel, page8);

    /// <summary>Encode one burst packet. <paramref name="sequenceChannel"/> is the packed (seq&lt;&lt;5)|channel byte.</summary>
    public static byte[] BurstData(byte sequenceChannel, ReadOnlySpan<byte> page8)
        => EncodeData(AntMessageId.BurstData, sequenceChannel, page8);

    private static byte[] EncodeData(AntMessageId id, byte channelByte, ReadOnlySpan<byte> page8)
    {
        if (page8.Length != 8)
            throw new ArgumentException("Data page must be exactly 8 bytes.", nameof(page8));
        Span<byte> data = stackalloc byte[9];
        data[0] = channelByte;
        page8.CopyTo(data[1..]);
        return AntFrame.Encode(id, data);
    }
}
