using Hilke.Ant.Model;

namespace Hilke.Ant.Plus;

/// <summary>Plus-facing mirror of the core library-level channel lifecycle state.</summary>
public enum AntPlusChannelState
{
    /// <summary>The channel has not been assigned/configured yet.</summary>
    Unconfigured,
    /// <summary>The channel is configured but not yet opened.</summary>
    Configured,
    /// <summary>The channel is open and searching for the remote device.</summary>
    Searching,
    /// <summary>The channel found the remote device and is actively receiving.</summary>
    Active,
    /// <summary>The channel was active but has not received a message within its inactivity timeout.</summary>
    Inactive,
    /// <summary>The channel is in the process of closing.</summary>
    Closing,
}

/// <summary>Raised when a connected profile's channel transitions between library-level states.</summary>
public sealed class AntPlusChannelStateChangedEventArgs : EventArgs
{
    /// <summary>Create the event args for a state transition.</summary>
    public AntPlusChannelStateChangedEventArgs(AntPlusChannelState oldState, AntPlusChannelState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    /// <summary>The state before the transition.</summary>
    public AntPlusChannelState OldState { get; }
    /// <summary>The state after the transition.</summary>
    public AntPlusChannelState NewState { get; }
}

internal static class AntPlusChannelStateExtensions
{
    internal static AntPlusChannelState ToPlus(this ChannelState s) => (AntPlusChannelState)(int)s;
}
