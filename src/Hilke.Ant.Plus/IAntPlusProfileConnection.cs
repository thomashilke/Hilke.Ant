namespace Hilke.Ant.Plus;

/// <summary>
/// Common surface every connected ANT+ profile monitor implements. Autonomously pumps incoming
/// messages from the moment it is returned by <see cref="AntPlusNode.ConnectAsync"/>; the client
/// never touches a raw channel or payload byte. <see cref="IAsyncDisposable.DisposeAsync"/> is the
/// sole teardown method: it performs the full graceful channel close/unassign sequence.
/// </summary>
public interface IAntPlusProfileConnection : IAsyncDisposable
{
    AntPlusDeviceId DeviceId { get; }
    byte ChannelNumber { get; }
    AntPlusChannelState State { get; }
    event EventHandler<AntPlusChannelStateChangedEventArgs>? StateChanged;
    event EventHandler<AntPlusTelemetryUpdate>? TelemetryUpdated;
}
