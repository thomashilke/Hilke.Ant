using Hilke.Ant.Plus.Common;

namespace Hilke.Ant.Plus;

/// <summary>
/// Common surface every connected ANT+ profile monitor implements. Autonomously pumps incoming
/// messages from the moment it is returned by <see cref="AntPlusNode.ConnectAsync"/>; the client
/// never touches a raw channel or payload byte. <see cref="IAsyncDisposable.DisposeAsync"/> is the
/// sole teardown method: it performs the full graceful channel close/unassign sequence.
/// </summary>
public interface IAntPlusProfileConnection : IAsyncDisposable
{
    /// <summary>The connected device's identity.</summary>
    AntPlusDeviceId DeviceId { get; }
    /// <summary>The ANT channel number assigned to this connection.</summary>
    byte ChannelNumber { get; }
    /// <summary>The connection's current channel lifecycle state.</summary>
    AntPlusChannelState State { get; }
    /// <summary>Raised on every channel lifecycle state transition.</summary>
    event EventHandler<AntPlusChannelStateChangedEventArgs>? StateChanged;
    /// <summary>Raised for every decoded telemetry update.</summary>
    event EventHandler<AntPlusTelemetryUpdate>? TelemetryUpdated;
    /// <summary>Raised for each received page that no decoder (profile-specific or common) recognized.</summary>
    event EventHandler<RawDataPage>? UnrecognizedPageReceived;
}
