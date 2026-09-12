namespace Hilke.Ant.Model;

/// <summary>Raised when a channel transitions between library-level states.</summary>
internal sealed class ChannelStateChangedEventArgs : EventArgs
{
    public ChannelStateChangedEventArgs(ChannelState oldState, ChannelState newState, ChannelTransitionReason reason)
    {
        OldState = oldState;
        NewState = newState;
        Reason = reason;
    }

    public ChannelState OldState { get; }
    public ChannelState NewState { get; }

    /// <summary>Why this transition happened; disambiguates same-looking (OldState, NewState) pairs
    /// with different causes (e.g. a radio-confirmed loss vs. a local inactivity timeout).</summary>
    public ChannelTransitionReason Reason { get; }
}
