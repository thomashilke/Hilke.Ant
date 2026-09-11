using Hilke.Ant.Plus;

namespace Hilke.Ant.Cli;

/// <summary>
/// Wraps one <see cref="AntPlusNode"/>: owns the scan session and every connected profile, and
/// enforces the hardware scan &lt;-&gt; channel mutual exclusion. Each connected profile pumps its
/// own messages; this session only tracks the mapping from token to profile and mirrors telemetry
/// into the <see cref="DeviceRegistry"/>.
/// </summary>
public sealed class AntSession : IAsyncDisposable
{
    private enum Mode { Idle, Scanning, Connected }

    private sealed class Connection
    {
        public required IAntPlusProfileConnection Profile { get; init; }
        public required EventHandler<AntPlusChannelStateChangedEventArgs> StateHandler { get; init; }
        public required EventHandler<AntPlusTelemetryUpdate> TelemetryHandler { get; init; }
    }

    private readonly AntPlusNode _node;
    private readonly DeviceRegistry _registry;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);

    private Mode _mode = Mode.Idle;
    private AntPlusScanSession? _scan;
    private CancellationTokenSource? _scanCts;
    private bool _disposed;

    public AntSession(AntPlusNode node, DeviceRegistry registry, Action<string> log)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
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

        var scan = await _node.StartScanAsync().ConfigureAwait(false);
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

    private async Task RunScanLoop(AntPlusScanSession scan, CancellationToken ct)
    {
        try
        {
            await foreach (var sighting in scan.ReceiveAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var e = _registry.GetOrAdd(sighting.DeviceId, sighting.ProfileName);
                    _registry.WithEntry(e.Token, x =>
                    {
                        Apply(x, sighting.Telemetry);
                        x.Rssi = sighting.Rssi;
                        x.LastSeen = sighting.At;
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
        AntPlusScanSession? scan;
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

        var profile = await _node.ConnectAsync(entry.Id).ConfigureAwait(false); // may throw NotSupportedException/AntPlusBusyException/AntPlusCommandException/AntPlusTimeoutException

        string tok = entry.Token;
        EventHandler<AntPlusChannelStateChangedEventArgs> stateHandler = (_, a) => _registry.WithEntry(tok, x => x.State = a.NewState);
        EventHandler<AntPlusTelemetryUpdate> telemetryHandler = (_, u) => _registry.WithEntry(tok, x => { Apply(x, u); x.LastSeen = DateTimeOffset.UtcNow; });
        profile.StateChanged += stateHandler;
        profile.TelemetryUpdated += telemetryHandler;
        lock (_gate)
        {
            _connections[tok] = new Connection { Profile = profile, StateHandler = stateHandler, TelemetryHandler = telemetryHandler };
            _mode = Mode.Connected;
        }
        _registry.WithEntry(tok, x =>
        {
            x.Connected = true;
            x.ChannelNumber = profile.ChannelNumber;
            x.State = profile.State;
        });
        _log($"Connected {tok} ({entry.ProfileName}) on channel {profile.ChannelNumber}.");
        return entry;
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

        try
        {
            await _node.DisconnectAsync(conn.Profile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"disconnect {tok}: {ex.Message}");
        }
        finally
        {
            conn.Profile.StateChanged -= conn.StateHandler;
            conn.Profile.TelemetryUpdated -= conn.TelemetryHandler;
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
            e.State = AntPlusChannelState.Unconfigured;
        });
        _log($"Disconnected {tok}.");
    }

    // ----- FE-C control -----

    public Task SetTargetPowerAsync(string token, ushort watts)
        => SendControlAsync(token, (fe, ct) => fe.SetTargetPowerAsync(watts, ct), $"target power {watts} W");

    public Task SetBasicResistanceAsync(string token, double percent)
        => SendControlAsync(token, (fe, ct) => fe.SetBasicResistanceAsync(percent, ct), $"resistance {percent:F0}%");

    private async Task SendControlAsync(string token, Func<FitnessEquipmentMonitor, CancellationToken, Task> op, string what)
    {
        FitnessEquipmentMonitor fe;
        string tok;
        lock (_gate)
        {
            if (!_registry.TryResolve(token, out var e) || !_connections.TryGetValue(e.Token, out var c))
                throw new InvalidOperationException($"Device '{token}' is not connected.");
            if (c.Profile is not FitnessEquipmentMonitor m)
                throw new InvalidOperationException($"'{e.Token}' is not an FE-C trainer.");
            fe = m;
            tok = e.Token;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await op(fe, cts.Token).ConfigureAwait(false);
            _log($"{tok}: {what} acknowledged.");
        }
        catch (OperationCanceledException)
        {
            _log($"{tok}: no acknowledgement from trainer for {what}.");
        }
    }

    // ----- Bicycle Power calibration -----

    public Task<PowerMeterCalibrationResult> RequestManualZeroAsync(string token, TimeSpan timeout)
        => RunCalibrationAsync(token, (bp, ct) => bp.RequestManualZeroAsync(timeout, ct));

    public Task<PowerMeterCalibrationResult> ConfigureAutoZeroAsync(string token, bool enable, TimeSpan timeout)
        => RunCalibrationAsync(token, (bp, ct) => bp.ConfigureAutoZeroAsync(enable, timeout, ct));

    private Task<PowerMeterCalibrationResult> RunCalibrationAsync(
        string token, Func<BicyclePowerMonitor, CancellationToken, Task<PowerMeterCalibrationResult>> op)
    {
        BicyclePowerMonitor monitor;
        lock (_gate)
        {
            if (!_registry.TryResolve(token, out var e) || !_connections.TryGetValue(e.Token, out var c))
                throw new InvalidOperationException($"Device '{token}' is not connected.");
            if (c.Profile is not BicyclePowerMonitor m)
                throw new InvalidOperationException($"'{e.Token}' is not a bicycle power meter.");
            monitor = m;
        }
        return op(monitor, default);
    }

    private static void Apply(TrackedDeviceEntry x, AntPlusTelemetryUpdate u)
    {
        if (u.HeartRate is { } hr) x.HeartRate = hr;
        if (u.PowerWatts is { } pw) x.PowerWatts = pw;
        if (u.AveragePower is { } avg) x.AveragePower = avg;
        if (u.Cadence is { } cad) x.Cadence = cad;
        if (u.SpeedMps is { } sp) x.SpeedMps = sp;
        if (u.TrainerStatus is { } ts) x.TrainerStatus = ts;
        if (u.Battery is { } bs) x.Battery = bs;
        if (u.BatteryVolts is { } bv) x.BatteryVolts = bv;
        if (u.Manufacturer is { } mfg) x.Manufacturer = mfg;
        if (u.Product is { } prod) x.Product = prod;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        List<Connection> conns;
        AntPlusScanSession? scan;
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
            try { await _node.DisconnectAsync(c.Profile).ConfigureAwait(false); } catch { /* best effort */ }
        }

        await _node.DisposeAsync().ConfigureAwait(false);
    }
}
