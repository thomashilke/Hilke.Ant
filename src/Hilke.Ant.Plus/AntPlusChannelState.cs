using Hilke.Ant.Model;

namespace Hilke.Ant.Plus;

/// <summary>Plus-facing mirror of the core library-level channel lifecycle state.</summary>
public enum AntPlusChannelState
{
    /// <summary>No channel has been assigned yet.</summary>
    Unconfigured,
    /// <summary>Assigned but not open (never opened, or closed - see <see cref="AntPlusChannelTransitionReason"/>
    /// on the transition into this state to tell an explicit close from an autonomous search timeout).</summary>
    Configured,
    /// <summary>Open, not currently receiving the remote device (first acquisition, or re-acquiring after <see cref="Lost"/>).</summary>
    Searching,
    /// <summary>Open and currently receiving the remote device.</summary>
    Tracking,
    /// <summary>Was <see cref="Tracking"/>; not receiving right now. See <see cref="AntPlusChannelTransitionReason"/>
    /// on the transition into this state to tell a radio-confirmed loss from a local inactivity-timeout heuristic.</summary>
    Lost,
    /// <summary>The channel is in the process of closing.</summary>
    Closing,
}

/// <summary>Why an <see cref="AntPlusChannelState"/> transition happened.</summary>
public enum AntPlusChannelTransitionReason
{
    /// <summary>Caused directly by an API call (connect/close/reconnect) completing.</summary>
    Requested,
    /// <summary>A message was received from the device (Searching/Lost -&gt; Tracking).</summary>
    DataReceived,
    /// <summary>The radio confirmed the device is gone and dropped to its own re-search (Tracking -&gt; Lost).</summary>
    DeviceLost,
    /// <summary>No message arrived within the profile's inactivity timeout; a local heuristic, not a
    /// radio-confirmed re-search (Tracking -&gt; Lost).</summary>
    InactivityTimeout,
    /// <summary>The radio gave up (re-)acquiring the device and closed the channel on its own (Searching/Lost -&gt; Configured).</summary>
    SearchTimedOut,
}

/// <summary>Raised when a connected profile's channel transitions between library-level states.</summary>
public sealed class AntPlusChannelStateChangedEventArgs : EventArgs
{
    /// <summary>Create the event args for a state transition.</summary>
    public AntPlusChannelStateChangedEventArgs(AntPlusChannelState oldState, AntPlusChannelState newState, AntPlusChannelTransitionReason reason)
    {
        OldState = oldState;
        NewState = newState;
        Reason = reason;
    }

    /// <summary>The state before the transition.</summary>
    public AntPlusChannelState OldState { get; }
    /// <summary>The state after the transition.</summary>
    public AntPlusChannelState NewState { get; }
    /// <summary>Why this transition happened; disambiguates same-looking (OldState, NewState) pairs
    /// with different causes (e.g. a radio-confirmed loss vs. a local inactivity timeout).</summary>
    public AntPlusChannelTransitionReason Reason { get; }
}

internal static class AntPlusChannelStateExtensions
{
    internal static AntPlusChannelState ToPlus(this ChannelState s) => s switch
    {
        ChannelState.Unconfigured => AntPlusChannelState.Unconfigured,
        ChannelState.Configured => AntPlusChannelState.Configured,
        ChannelState.Searching => AntPlusChannelState.Searching,
        ChannelState.Tracking => AntPlusChannelState.Tracking,
        ChannelState.Lost => AntPlusChannelState.Lost,
        ChannelState.Closing => AntPlusChannelState.Closing,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "Unmapped ChannelState."),
    };

    internal static AntPlusChannelTransitionReason ToPlus(this ChannelTransitionReason r) => r switch
    {
        ChannelTransitionReason.Requested => AntPlusChannelTransitionReason.Requested,
        ChannelTransitionReason.DataReceived => AntPlusChannelTransitionReason.DataReceived,
        ChannelTransitionReason.DeviceLost => AntPlusChannelTransitionReason.DeviceLost,
        ChannelTransitionReason.InactivityTimeout => AntPlusChannelTransitionReason.InactivityTimeout,
        ChannelTransitionReason.SearchTimedOut => AntPlusChannelTransitionReason.SearchTimedOut,
        _ => throw new ArgumentOutOfRangeException(nameof(r), r, "Unmapped ChannelTransitionReason."),
    };
}
