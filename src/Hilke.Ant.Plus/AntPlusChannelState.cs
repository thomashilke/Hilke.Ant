using Hilke.Ant.Model;

namespace Hilke.Ant.Plus;

/// <summary>Plus-facing mirror of the core library-level channel lifecycle state.</summary>
public enum AntPlusChannelState
{
    Unconfigured,
    Configured,
    Searching,
    Active,
    Inactive,
    Closing,
}

/// <summary>Raised when a connected profile's channel transitions between library-level states.</summary>
public sealed class AntPlusChannelStateChangedEventArgs : EventArgs
{
    public AntPlusChannelStateChangedEventArgs(AntPlusChannelState oldState, AntPlusChannelState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    public AntPlusChannelState OldState { get; }
    public AntPlusChannelState NewState { get; }
}

internal static class AntPlusChannelStateExtensions
{
    internal static AntPlusChannelState ToPlus(this ChannelState s) => (AntPlusChannelState)(int)s;
}
