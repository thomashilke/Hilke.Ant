using Hilke.Ant.Protocol;

namespace Hilke.Ant.Model;

/// <summary>Library-level channel lifecycle state (distinct from device <see cref="DeviceChannelState"/>).</summary>
internal enum ChannelState
{
    /// <summary>No channel has been assigned yet.</summary>
    Unconfigured,
    /// <summary>Assigned but not open (never opened, or closed - see <see cref="ChannelTransitionReason"/> on the transition into this state to tell an explicit close from an autonomous search timeout).</summary>
    Configured,
    /// <summary>Open, not currently receiving the remote device (first acquisition, or re-acquiring after <see cref="Lost"/>).</summary>
    Searching,
    /// <summary>Open and currently receiving the remote device.</summary>
    Tracking,
    /// <summary>Was <see cref="Tracking"/>; not receiving right now. The channel may or may not have actually
    /// dropped to a radio-level re-search yet - see <see cref="ChannelTransitionReason"/> on the transition
    /// into this state to tell a radio-confirmed loss from the local inactivity-timeout heuristic.</summary>
    Lost,
    /// <summary>An explicit <c>CloseAsync</c> is in flight, awaiting the device's confirmation.</summary>
    Closing,
}

/// <summary>Why a <see cref="ChannelState"/> transition happened.</summary>
internal enum ChannelTransitionReason
{
    /// <summary>Caused directly by an API call (Open/Close/Unassign) completing.</summary>
    Requested,
    /// <summary>A message was received from the device (Searching/Lost -&gt; Tracking).</summary>
    DataReceived,
    /// <summary>The radio reported <c>EventRxFailGoToSearch</c>: it confirmed the device is gone and dropped to its own re-search (Tracking -&gt; Lost).</summary>
    DeviceLost,
    /// <summary>No message arrived within <see cref="ChannelConfiguration.InactivityTimeout"/>; a local heuristic, not a radio-confirmed re-search (Tracking -&gt; Lost).</summary>
    InactivityTimeout,
    /// <summary>The radio reported <c>EventRxSearchTimeout</c>: it gave up (re-)acquiring the device and closed the channel on its own (Searching/Lost -&gt; Configured).</summary>
    SearchTimedOut,
}

/// <summary>Immutable configuration applied when assigning/opening a channel.</summary>
internal sealed record ChannelConfiguration
{
    public required ChannelType Type { get; init; }
    public byte NetworkNumber { get; init; }
    public required ChannelId ChannelId { get; init; }
    public required byte RfFrequency { get; init; }
    public required ushort ChannelPeriod { get; init; }
    public byte SearchTimeout { get; init; } = AntConstants.WildcardSearchTimeout;

    /// <summary>Optional library-side inactivity timeout: drives <see cref="ChannelState.Tracking"/> → <see cref="ChannelState.Lost"/> (reason <see cref="ChannelTransitionReason.InactivityTimeout"/>).</summary>
    public TimeSpan? InactivityTimeout { get; init; }

    /// <summary>Optional per-channel transmit power level (0..4); null leaves the device default.</summary>
    public sbyte? TransmitPower { get; init; }

    /// <summary>Enable extended RX messages (appended channel id / RSSI) via LibConfig + EnableExtRx.</summary>
    public bool UseExtendedMessages { get; init; }
}

/// <summary>Configuration for a continuous-scan session (Open Rx Scan Mode, channel 0).</summary>
internal sealed record ScanConfiguration
{
    public byte NetworkNumber { get; init; }
    public byte RfFrequency { get; init; }
    public ChannelId ChannelId { get; init; } = ChannelId.Wildcard();
    public bool UseExtendedMessages { get; init; } = true;
}
