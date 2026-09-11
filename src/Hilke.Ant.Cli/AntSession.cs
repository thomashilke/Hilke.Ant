using Hilke.Ant.Model;
using Hilke.Ant.Plus;

namespace Hilke.Ant.Cli;

/// <summary>
/// Wraps one <see cref="AntDevice"/>: owns the scan session and per-connection receive loops and
/// enforces the hardware scan &lt;-&gt; channel mutual exclusion. Each channel's single-consumer
/// <c>ReceiveAsync</c> stream is drained by exactly one loop, which dispatches pages to the decoders
/// via <see cref="ProfileCatalog.Update"/>.
/// </summary>
public sealed class AntSession : IAsyncDisposable
{
    private enum Mode { Idle, Scanning, Connected }

    private sealed class Connection
    {
        public required AntChannel Channel { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required EventHandler<ChannelStateChangedEventArgs> StateHandler { get; init; }
        public PowerMeterCalibrationSession? Calibration { get; init; }
    }

    private readonly AntDevice _device;
    private readonly DeviceRegistry _registry;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);

    private Mode _mode = Mode.Idle;
    private ScanSession? _scan;
    private CancellationTokenSource? _scanCts;
    private bool _disposed;

    public AntSession(AntDevice device, DeviceRegistry registry, Action<string> log)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>True while a continuous-scan session is active.</summary>
    public bool IsScanning
    {
        get { lock (_gate) return _mode == Mode.Scanning; }
    }

    public string ModeLabel
    {
        get { lock (_gate) return _mode.ToString().ToLowerInvariant(); }
    }

    // ----- scan -----

    public async Task StartScanAsync()
    {
        lock (_gate)
        {
            if (_mode == Mode.Scanning)
                return;
            if (_connections.Count > 0)
                throw new InvalidOperationException("Disconnect all devices before scanning.");
        }

        var scan = await _device.StartScanAsync(new ScanConfiguration()).ConfigureAwait(false);
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _scan = scan;
            _scanCts = cts;
            _mode = Mode.Scanning;
        }
        _ = RunScanLoop(scan, cts.Token);
        _log("Scanning for ANT+ devices.");
    }

    private async Task RunScanLoop(ScanSession scan, CancellationToken ct)
    {
        try
        {
            await foreach (var m in scan.ReceiveAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var e = _registry.GetOrAdd(m.Device, ProfileCatalog.ProfileNameOrUnknown, ProfileCatalog.CreateDecoderSet);
                    _registry.WithEntry(e.Token, x =>
                    {
                        ProfileCatalog.Update(x, m.Payload.Span);
                        x.Rssi = m.Rssi;
                        x.LastSeen = DateTimeOffset.UtcNow;
                    });
                }
                catch (Exception ex)
                {
                    _log($"scan decode error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // scan stopped
        }
        catch (Exception ex)
        {
            _log($"scan loop ended: {ex.Message}");
        }
    }

    public async Task StopScanAsync()
    {
        ScanSession? scan;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_mode != Mode.Scanning)
                return;
            scan = _scan;
            cts = _scanCts;
            _scan = null;
            _scanCts = null;
            _mode = _connections.Count > 0 ? Mode.Connected : Mode.Idle;
        }
        cts?.Cancel();
        if (scan is not null)
            await scan.StopAsync().ConfigureAwait(false);
        cts?.Dispose();
        _log("Scan stopped.");
    }

    // ----- connections -----

    public async Task<TrackedDeviceEntry> ConnectAsync(string token)
    {
        if (!_registry.TryResolve(token, out var entry))
            throw new InvalidOperationException($"Unknown device '{token}'.");

        lock (_gate)
        {
            if (_connections.ContainsKey(entry.Token))
                throw new InvalidOperationException($"Device '{entry.Token}' is already connected.");
        }

        if (IsScanning)
            await StopScanAsync().ConfigureAwait(false);

        var config = ProfileCatalog.BuildConfig(entry.DeviceType, entry.Id); // may throw NotSupportedException

        byte channelNumber = AllocateChannel();
        var channel = await _device.ConfigureChannelAsync(channelNumber, config).ConfigureAwait(false);

        string tok = entry.Token;
        EventHandler<ChannelStateChangedEventArgs> handler = (_, a) =>
            _registry.WithEntry(tok, x => x.State = a.NewState);
        channel.StateChanged += handler;

        await channel.OpenAsync().ConfigureAwait(false);

        var calibration = entry.DeviceType == BicyclePowerMonitor.DeviceType
            ? new PowerMeterCalibrationSession(channel)
            : null;

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _connections[tok] = new Connection { Channel = channel, Cts = cts, StateHandler = handler, Calibration = calibration };
            _mode = Mode.Connected;
        }
        _registry.WithEntry(tok, x =>
        {
            x.Connected = true;
            x.ChannelNumber = channelNumber;
            x.State = channel.State;
        });
        _ = RunChannelLoop(tok, channel, calibration, cts.Token);
        _log($"Connected {tok} ({entry.ProfileName}) on channel {channelNumber}.");
        return entry;
    }

    private byte AllocateChannel()
    {
        byte max = _device.Capabilities.MaxChannels;
        if (max == 0)
            max = 8; // conservative default before capabilities are known
        lock (_gate)
        {
            var used = new HashSet<byte>();
            foreach (var c in _connections.Values)
                used.Add(c.Channel.ChannelNumber);
            for (byte n = 1; n < max; n++)
            {
                if (!used.Contains(n))
                    return n;
            }
        }
        throw new InvalidOperationException("All channels in use.");
    }

    private async Task RunChannelLoop(string token, AntChannel channel, PowerMeterCalibrationSession? calibration, CancellationToken ct)
    {
        try
        {
            await foreach (var m in channel.ReceiveAsync(ct).ConfigureAwait(false))
            {
                calibration?.HandleData(m.Payload.Span, m.ReceivedAt);
                try
                {
                    _registry.WithEntry(token, x =>
                    {
                        ProfileCatalog.Update(x, m.Payload.Span);
                        if (m.Rssi is { } r)
                            x.Rssi = r;
                        x.LastSeen = DateTimeOffset.UtcNow;
                    });
                }
                catch (Exception ex)
                {
                    _log($"{token} decode error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disconnected
        }
        catch (Exception ex)
        {
            _log($"{token} loop ended: {ex.Message}");
        }
    }

    public async Task DisconnectAsync(string token)
    {
        Connection? conn;
        string tok = token;
        lock (_gate)
        {
            if (!_registry.TryResolve(token, out var e))
            {
                conn = null;
            }
            else
            {
                tok = e.Token;
                _connections.TryGetValue(tok, out conn);
            }
        }
        if (conn is null)
        {
            _log($"'{token}' is not connected.");
            return;
        }

        conn.Cts.Cancel();
        try
        {
            await conn.Channel.CloseAsync().ConfigureAwait(false);
            await conn.Channel.UnassignAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"disconnect {tok}: {ex.Message}");
        }
        finally
        {
            conn.Channel.StateChanged -= conn.StateHandler;
            await conn.Channel.DisposeAsync().ConfigureAwait(false);
            conn.Cts.Dispose();
        }

        lock (_gate)
        {
            _connections.Remove(tok);
            if (_connections.Count == 0 && _mode == Mode.Connected)
                _mode = Mode.Idle;
        }
        _registry.WithEntry(tok, e =>
        {
            e.Connected = false;
            e.ChannelNumber = null;
            e.State = ChannelState.Unconfigured;
        });
        _log($"Disconnected {tok}.");
    }

    // ----- FE-C control -----

    public Task SetTargetPowerAsync(string token, ushort watts)
        => SendControlAsync(token, FitnessEquipmentMonitor.BuildTargetPowerPage(watts), $"target power {watts} W");

    public Task SetBasicResistanceAsync(string token, double percent)
        => SendControlAsync(token, FitnessEquipmentMonitor.BuildBasicResistancePage(percent), $"resistance {percent:F0}%");

    private async Task SendControlAsync(string token, byte[] page, string what)
    {
        Connection conn;
        string tok;
        lock (_gate)
        {
            if (!_registry.TryResolve(token, out var e) || !_connections.TryGetValue(e.Token, out var c))
                throw new InvalidOperationException($"Device '{token}' is not connected.");
            if (e.DeviceType != FitnessEquipmentMonitor.DeviceType)
                throw new InvalidOperationException($"'{e.Token}' is not an FE-C trainer.");
            conn = c;
            tok = e.Token;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await conn.Channel.SendAcknowledgedAsync(page, cts.Token).ConfigureAwait(false);
            _log($"{tok}: {what} acknowledged.");
        }
        catch (OperationCanceledException)
        {
            _log($"{tok}: no acknowledgement from trainer for {what}.");
        }
    }

    // ----- Bicycle Power calibration -----

    public Task<PowerMeterCalibrationResult> RequestManualZeroAsync(string token, TimeSpan timeout)
        => RunCalibrationAsync(token, s => s.RequestManualZeroAsync(timeout));

    public Task<PowerMeterCalibrationResult> ConfigureAutoZeroAsync(string token, bool enable, TimeSpan timeout)
        => RunCalibrationAsync(token, s => s.ConfigureAutoZeroAsync(enable, timeout));

    private Task<PowerMeterCalibrationResult> RunCalibrationAsync(
        string token, Func<PowerMeterCalibrationSession, Task<PowerMeterCalibrationResult>> op)
    {
        PowerMeterCalibrationSession session;
        lock (_gate)
        {
            if (!_registry.TryResolve(token, out var e) || !_connections.TryGetValue(e.Token, out var c))
                throw new InvalidOperationException($"Device '{token}' is not connected.");
            if (c.Calibration is null)
                throw new InvalidOperationException($"'{e.Token}' is not a bicycle power meter.");
            session = c.Calibration;
        }
        return op(session);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        List<Connection> conns;
        ScanSession? scan;
        CancellationTokenSource? scanCts;
        lock (_gate)
        {
            conns = _connections.Values.ToList();
            _connections.Clear();
            scan = _scan;
            scanCts = _scanCts;
            _scan = null;
            _scanCts = null;
            _mode = Mode.Idle;
        }

        scanCts?.Cancel();
        if (scan is not null)
        {
            try { await scan.StopAsync().ConfigureAwait(false); } catch { /* best effort */ }
        }
        scanCts?.Dispose();

        foreach (var c in conns)
        {
            c.Cts.Cancel();
            try { await c.Channel.CloseAsync().ConfigureAwait(false); } catch { /* best effort */ }
            try { await c.Channel.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
            c.Cts.Dispose();
        }

        await _device.DisposeAsync().ConfigureAwait(false);
    }
}
