using Hilke.Ant.Protocol;

namespace Hilke.Ant.Model;

/// <summary>Library-level channel lifecycle state (distinct from device <see cref="DeviceChannelState"/>).</summary>
public enum ChannelState
{
    Unconfigured,
    Configured,
    Searching,
    Active,
    Inactive,
    Closing,
}

/// <summary>Immutable configuration applied when assigning/opening a channel.</summary>
public sealed record ChannelConfiguration
{
    public required ChannelType Type { get; init; }
    public byte NetworkNumber { get; init; }
    public required ChannelId ChannelId { get; init; }
    public required byte RfFrequency { get; init; }
    public required ushort ChannelPeriod { get; init; }
    public byte SearchTimeout { get; init; } = AntConstants.WildcardSearchTimeout;

    /// <summary>Optional library-side inactivity timeout: drives <see cref="ChannelState.Active"/> → <see cref="ChannelState.Inactive"/>.</summary>
    public TimeSpan? InactivityTimeout { get; init; }

    /// <summary>Optional per-channel transmit power level (0..4); null leaves the device default.</summary>
    public sbyte? TransmitPower { get; init; }

    /// <summary>Enable extended RX messages (appended channel id / RSSI) via LibConfig + EnableExtRx.</summary>
    public bool UseExtendedMessages { get; init; }
}

/// <summary>Configuration for a continuous-scan session (Open Rx Scan Mode, channel 0).</summary>
public sealed record ScanConfiguration
{
    public byte NetworkNumber { get; init; }
    public byte RfFrequency { get; init; }
    public ChannelId ChannelId { get; init; } = ChannelId.Wildcard();
    public bool UseExtendedMessages { get; init; } = true;
}
