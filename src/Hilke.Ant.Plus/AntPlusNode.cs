using Hilke.Ant.Model;
using Hilke.Ant.Transport;

namespace Hilke.Ant.Plus;

/// <summary>
/// The top-level ANT+ facade. Owns the core <see cref="AntDevice"/> (never exposed), opens it with
/// the ANT+ managed-network key, and is the sole ANT+-facing entry point for scan/connect/disconnect/
/// dispose. A client never touches a raw channel, a raw payload byte, or a core exception/enum type.
/// </summary>
public sealed class AntPlusNode : IAsyncDisposable
{
    private readonly AntDevice _device;
    private readonly object _gate = new();
    private readonly Dictionary<byte, IAntPlusProfileConnection> _connections = new();

    private AntPlusNode(AntDevice device) => _device = device;

    public static async Task<AntPlusNode> OpenAsync(IAntTransport transport, CancellationToken ct = default)
    {
        var device = new AntDevice(transport);
        try
        {
            await device.OpenAsync(ct).ConfigureAwait(false);
            await device.SetNetworkKeyAsync(AntPlusProtocol.NetworkNumber, AntPlusProtocol.NetworkKey, ct).ConfigureAwait(false);
        }
        catch (AntCommandException ex) { await device.DisposeAsync().ConfigureAwait(false); throw new AntPlusCommandException(ex.Message); }
        catch (AntTimeoutException ex) { await device.DisposeAsync().ConfigureAwait(false); throw new AntPlusTimeoutException(ex.Message); }
        return new AntPlusNode(device);
    }

    public async Task<AntPlusScanSession> StartScanAsync(CancellationToken ct = default)
    {
        try
        {
            var cfg = new ScanConfiguration { NetworkNumber = AntPlusProtocol.NetworkNumber, RfFrequency = AntPlusProtocol.RfFrequency };
            return new AntPlusScanSession(await _device.StartScanAsync(cfg, ct).ConfigureAwait(false));
        }
        catch (RadioBusyException ex) { throw new AntPlusBusyException(ex.Message); }
    }

    public async Task<IAntPlusProfileConnection> ConnectAsync(AntPlusDeviceId id, CancellationToken ct = default)
    {
        byte channelNumber;
        lock (_gate) { channelNumber = AllocateChannelLocked(); }

        ChannelConfiguration config = id.DeviceType switch
        {
            HeartRateMonitor.DeviceType => HeartRateMonitor.SlaveDefaults(id.ToCore()),
            BicyclePowerMonitor.DeviceType => BicyclePowerMonitor.SlaveDefaults(id.ToCore()),
            FitnessEquipmentMonitor.DeviceType => FitnessEquipmentMonitor.SlaveDefaults(id.ToCore()),
            _ => throw new NotSupportedException($"No ANT+ profile for device type {id.DeviceType}"),
        };
        AntChannel channel;
        try { channel = await _device.ConfigureChannelAsync(channelNumber, config, ct).ConfigureAwait(false); }
        catch (RadioBusyException ex) { throw new AntPlusBusyException(ex.Message); }
        catch (AntCommandException ex) { throw new AntPlusCommandException(ex.Message); }
        catch (AntTimeoutException ex) { throw new AntPlusTimeoutException(ex.Message); }

        try { await channel.OpenAsync(ct).ConfigureAwait(false); }
        catch (AntCommandException ex) { throw new AntPlusCommandException(ex.Message); }
        catch (AntTimeoutException ex) { throw new AntPlusTimeoutException(ex.Message); }
        catch (InvalidChannelStateException ex) { throw new AntPlusCommandException(ex.Message); }

        IAntPlusProfileConnection profile = id.DeviceType switch
        {
            HeartRateMonitor.DeviceType => new HeartRateMonitor(channel),
            BicyclePowerMonitor.DeviceType => new BicyclePowerMonitor(channel),
            FitnessEquipmentMonitor.DeviceType => new FitnessEquipmentMonitor(channel),
            _ => throw new NotSupportedException($"No ANT+ profile for device type {id.DeviceType}"),
        };
        lock (_gate) { _connections[channelNumber] = profile; }
        return profile;
    }

    public async Task DisconnectAsync(IAntPlusProfileConnection connection, CancellationToken ct = default)
    {
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        finally { lock (_gate) { _connections.Remove(connection.ChannelNumber); } }
    }

    private byte AllocateChannelLocked()
    {
        byte max = _device.Capabilities.MaxChannels;
        if (max == 0) max = 8;
        for (byte n = 1; n < max; n++)
            if (!_connections.ContainsKey(n)) return n;
        throw new InvalidOperationException("All channels in use.");
    }

    public async ValueTask DisposeAsync() => await _device.DisposeAsync().ConfigureAwait(false);
}
