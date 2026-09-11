namespace Hilke.Ant.Model;

/// <summary>Raised when a channel transitions between library-level states.</summary>
internal sealed class ChannelStateChangedEventArgs : EventArgs
{
    public ChannelStateChangedEventArgs(ChannelState oldState, ChannelState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    public ChannelState OldState { get; }
    public ChannelState NewState { get; }
}
