using Hilke.Ant.Protocol;

namespace Hilke.Ant.Model;

/// <summary>A received data message surfaced to consumers.</summary>
public class AntDataMessage
{
    public AntDataMessage(ChannelId? deviceId, DataKind kind, ReadOnlyMemory<byte> payload, sbyte? rssi, DateTimeOffset receivedAt)
    {
        DeviceId = deviceId;
        Kind = kind;
        Payload = payload;
        Rssi = rssi;
        ReceivedAt = receivedAt;
    }

    /// <summary>Device identity when extended messages are enabled; otherwise null.</summary>
    public ChannelId? DeviceId { get; }
    public DataKind Kind { get; }

    /// <summary>The 8-byte data page.</summary>
    public ReadOnlyMemory<byte> Payload { get; }
    public sbyte? Rssi { get; }
    public DateTimeOffset ReceivedAt { get; }
}

/// <summary>A scan-mode data message; <see cref="AntDataMessage.DeviceId"/> is always present.</summary>
public sealed class ScanDataMessage : AntDataMessage
{
    public ScanDataMessage(ChannelId deviceId, DataKind kind, ReadOnlyMemory<byte> payload, sbyte? rssi, DateTimeOffset receivedAt)
        : base(deviceId, kind, payload, rssi, receivedAt)
    {
        Device = deviceId;
    }

    /// <summary>Non-nullable accessor for the scanning device identity.</summary>
    public ChannelId Device { get; }
}
