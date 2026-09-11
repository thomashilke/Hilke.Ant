using Hilke.Ant.Model;

namespace Hilke.Ant.Protocol.Messages;

/// <summary>Channel Response / Event (0x40): DATA = [channel, responseToId, code].</summary>
internal readonly struct ChannelResponse
{
    public ChannelResponse(byte channel, AntMessageId responseToId, ChannelResponseCode code)
    {
        Channel = channel;
        ResponseToId = responseToId;
        Code = code;
    }

    public byte Channel { get; }
    public AntMessageId ResponseToId { get; }
    public ChannelResponseCode Code { get; }

    /// <summary>True when this is an RF event rather than a response to a command (responseToId == 1).</summary>
    public bool IsEvent => (byte)ResponseToId == AntConstants.EventResponseMarker;
}

/// <summary>A received data message (0x4E broadcast, 0x4F acknowledged, 0x50 burst).</summary>
internal readonly struct ReceivedData
{
    public ReceivedData(byte channel, DataKind kind, byte[] payload, ChannelId? extendedId, sbyte? rssi, ushort? rxTimestamp)
    {
        Channel = channel;
        Kind = kind;
        Payload = payload;
        ExtendedId = extendedId;
        Rssi = rssi;
        RxTimestamp = rxTimestamp;
    }

    public byte Channel { get; }
    public DataKind Kind { get; }

    /// <summary>The 8-byte data page.</summary>
    public byte[] Payload { get; }
    public ChannelId? ExtendedId { get; }
    public sbyte? Rssi { get; }
    public ushort? RxTimestamp { get; }
}

/// <summary>Startup message (0x6F): a single flags byte describing the reset reason.</summary>
internal readonly struct Startup
{
    public Startup(byte flags) => Flags = flags;
    public byte Flags { get; }
}

/// <summary>Capabilities message (0x54).</summary>
internal readonly struct CapabilitiesMessage
{
    public CapabilitiesMessage(byte maxChannels, byte maxNetworks, byte standardOptions, byte advancedOptions)
    {
        MaxChannels = maxChannels;
        MaxNetworks = maxNetworks;
        StandardOptions = standardOptions;
        AdvancedOptions = advancedOptions;
    }

    public byte MaxChannels { get; }
    public byte MaxNetworks { get; }
    public byte StandardOptions { get; }
    public byte AdvancedOptions { get; }
}

/// <summary>Channel Status message (0x52): low 2 bits of the status byte are the device state.</summary>
internal readonly struct ChannelStatusMessage
{
    public ChannelStatusMessage(byte channel, DeviceChannelState state)
    {
        Channel = channel;
        State = state;
    }

    public byte Channel { get; }
    public DeviceChannelState State { get; }
}

/// <summary>Channel Id message (0x51).</summary>
internal readonly struct ChannelIdMessage
{
    public ChannelIdMessage(byte channel, ChannelId id)
    {
        Channel = channel;
        Id = id;
    }

    public byte Channel { get; }
    public ChannelId Id { get; }
}

/// <summary>Parsers producing typed results from raw <see cref="AntMessage"/> values.</summary>
internal static class InboundMessages
{
    public static ChannelResponse ParseChannelResponse(AntMessage msg)
    {
        var p = msg.Payload.Span;
        if (p.Length < 3)
            throw new AntException($"Channel response too short: {p.Length} bytes.");
        return new ChannelResponse(p[0], (AntMessageId)p[1], (ChannelResponseCode)p[2]);
    }

    public static Startup ParseStartup(AntMessage msg)
    {
        var p = msg.Payload.Span;
        return new Startup(p.Length > 0 ? p[0] : (byte)0);
    }

    public static CapabilitiesMessage ParseCapabilities(AntMessage msg)
    {
        var p = msg.Payload.Span;
        if (p.Length < 2)
            throw new AntException($"Capabilities message too short: {p.Length} bytes.");
        byte std = p.Length > 2 ? p[2] : (byte)0;
        byte adv = p.Length > 3 ? p[3] : (byte)0;
        return new CapabilitiesMessage(p[0], p[1], std, adv);
    }

    public static ChannelStatusMessage ParseChannelStatus(AntMessage msg)
    {
        var p = msg.Payload.Span;
        if (p.Length < 2)
            throw new AntException($"Channel status message too short: {p.Length} bytes.");
        return new ChannelStatusMessage(p[0], (DeviceChannelState)(p[1] & 0x03));
    }

    public static ChannelIdMessage ParseChannelId(AntMessage msg)
    {
        var p = msg.Payload.Span;
        if (p.Length < 5)
            throw new AntException($"Channel id message too short: {p.Length} bytes.");
        var id = new ChannelId((ushort)(p[1] | (p[2] << 8)), p[3], p[4]);
        return new ChannelIdMessage(p[0], id);
    }

    /// <summary>Parse a broadcast/acknowledged/burst data message, including any extended fields.</summary>
    public static ReceivedData ParseReceivedData(AntMessage msg)
    {
        DataKind kind = msg.Id switch
        {
            AntMessageId.BroadcastData => DataKind.Broadcast,
            AntMessageId.AcknowledgedData => DataKind.Acknowledged,
            AntMessageId.BurstData => DataKind.Burst,
            _ => throw new AntException($"Message {msg.Id} is not a data message."),
        };

        var p = msg.Payload.Span;
        if (p.Length < 9)
            throw new AntException($"Data message too short: {p.Length} bytes.");

        byte channel = p[0];
        var payload = p.Slice(1, 8).ToArray();

        ChannelId? extendedId = null;
        sbyte? rssi = null;
        ushort? timestamp = null;

        if (p.Length > 9)
        {
            byte flags = p[9];
            int o = 10;
            if ((flags & AntConstants.LibConfigChannelId) != 0 && o + 4 <= p.Length)
            {
                extendedId = new ChannelId((ushort)(p[o] | (p[o + 1] << 8)), p[o + 2], p[o + 3]);
                o += 4;
            }
            if ((flags & AntConstants.LibConfigRssi) != 0 && o + 3 <= p.Length)
            {
                // [measurementType, rssiValue (sbyte), thresholdConfig]
                rssi = (sbyte)p[o + 1];
                o += 3;
            }
            if ((flags & AntConstants.LibConfigRxTimestamp) != 0 && o + 2 <= p.Length)
            {
                timestamp = (ushort)(p[o] | (p[o + 1] << 8));
                o += 2;
            }
        }

        return new ReceivedData(channel, kind, payload, extendedId, rssi, timestamp);
    }
}
