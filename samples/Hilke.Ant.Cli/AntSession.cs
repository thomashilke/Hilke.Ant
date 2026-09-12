using Hilke.Ant.Plus;
using Hilke.Ant.Plus.BicyclePower;
using Hilke.Ant.Plus.FitnessEquipment;
using Hilke.Ant.Plus.Common;

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
        public required EventHandler<RawDataPage> UnrecognizedPageHandler { get; init; }
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
        EventHandler<AntPlusChannelStateChangedEventArgs> stateHandler = (_, a) => _registry.WithEntry(tok, x => { x.State = a.NewState; x.LastTransitionReason = a.Reason; });
        EventHandler<AntPlusTelemetryUpdate> telemetryHandler = (_, u) => _registry.WithEntry(tok, x => { Apply(x, u); x.LastSeen = DateTimeOffset.UtcNow; });
        // Dedupe by page number: log the first sighting of each unrecognized page per connection
        // rather than flooding the log at the sensor's broadcast rate (up to 8Hz).
        var seenUnrecognizedPages = new HashSet<byte>();
        EventHandler<RawDataPage> unrecognizedHandler = (_, p) =>
        {
            if (seenUnrecognizedPages.Add(p.PageNumber))
                _log($"{tok}: unrecognized page 0x{p.PageNumber:X2}: {Convert.ToHexString(p.Payload)}");
        };
        profile.StateChanged += stateHandler;
        profile.TelemetryUpdated += telemetryHandler;
        profile.UnrecognizedPageReceived += unrecognizedHandler;
        lock (_gate)
        {
            _connections[tok] = new Connection { Profile = profile, StateHandler = stateHandler, TelemetryHandler = telemetryHandler, UnrecognizedPageHandler = unrecognizedHandler };
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
            conn.Profile.UnrecognizedPageReceived -= conn.UnrecognizedPageHandler;
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
            e.LastTransitionReason = null;
        });
        _log($"Disconnected {tok}.");
    }

    // ----- FE-C control -----

    public Task SetTargetPowerAsync(string token, ushort watts)
        => SendControlAsync(token, FitnessEquipmentMonitor.TargetPowerPage, (fe, ct) => fe.SetTargetPowerAsync(watts, ct), $"target power {watts} W");

    public Task SetBasicResistanceAsync(string token, double percent)
        => SendControlAsync(token, FitnessEquipmentMonitor.BasicResistancePage, (fe, ct) => fe.SetBasicResistanceAsync(percent, ct), $"resistance {percent:F0}%");

    private async Task SendControlAsync(string token, byte commandPageId, Func<FitnessEquipmentMonitor, CancellationToken, Task> op, string what)
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

        // The ANT-level ack below only confirms the bytes reached the trainer's radio, not that the
        // FE-C application accepted them; subscribe for the trainer's own Command Status page (0x47)
        // first so a fast reply can't race the subscription.
        var statusTcs = new TaskCompletionSource<FitnessEquipmentCommandStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<FitnessEquipmentCommandResult> onStatus = (_, r) =>
        {
            if (r.LastReceivedCommandId == commandPageId)
                statusTcs.TrySetResult(r.Status);
        };
        fe.CommandStatusReceived += onStatus;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await op(fe, cts.Token).ConfigureAwait(false);

            using var statusCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var reg = statusCts.Token.Register(() => statusTcs.TrySetResult(FitnessEquipmentCommandStatus.Uninitialized));
            var status = await statusTcs.Task.ConfigureAwait(false);
            _log($"{tok}: {what} -> {FormatCommandStatus(status)}.");
        }
        catch (OperationCanceledException)
        {
            _log($"{tok}: no acknowledgement from trainer for {what}.");
        }
        finally
        {
            fe.CommandStatusReceived -= onStatus;
        }
    }

    private static string FormatCommandStatus(FitnessEquipmentCommandStatus status) => status switch
    {
        FitnessEquipmentCommandStatus.Pass => "accepted by trainer",
        FitnessEquipmentCommandStatus.Fail => "rejected by trainer (fail)",
        FitnessEquipmentCommandStatus.NotSupported => "not supported by trainer",
        FitnessEquipmentCommandStatus.Rejected => "rejected by trainer",
        FitnessEquipmentCommandStatus.Pending => "pending",
        FitnessEquipmentCommandStatus.Uninitialized => "sent, but trainer did not confirm (no command-status page received)",
        _ => "sent",
    };

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

    /// <summary>Query and enable Cycling Dynamics features on a connected bicycle power meter, subscribing to its telemetry on success.</summary>
    public async Task<CyclingDynamicsCapabilities> EnableCyclingDynamicsAsync(string token, CyclingDynamicsFeatures features, TimeSpan timeout)
    {
        BicyclePowerMonitor power;
        string tok;
        lock (_gate)
        {
            if (!_registry.TryResolve(token, out var e) || !_connections.TryGetValue(e.Token, out var c))
                throw new InvalidOperationException($"Device '{token}' is not connected.");
            if (c.Profile is not BicyclePowerMonitor m)
                throw new InvalidOperationException($"'{e.Token}' is not a power meter.");
            power = m;
            tok = e.Token;
        }
        var result = await power.EnableCyclingDynamicsAsync(features, timeout).ConfigureAwait(false);
        if (result.Result is CyclingDynamicsResult.Enabled or CyclingDynamicsResult.PartiallyEnabled)
        {
            power.PedalForceAngleReceived += (_, a) => _log($"{tok}: pedal-force {a.Side} start={a.StartAngleDegrees:F0}deg end={a.EndAngleDegrees:F0}deg torque={a.TorqueNewtonMeters:F1}Nm");
            power.PedalPositionReceived += (_, p) => _log($"{tok}: pedal-position {p.Position} cadence={p.CadenceRpm}rpm pco(R/L)={p.RightPlatformCenterOffsetMm}/{p.LeftPlatformCenterOffsetMm}mm");
            power.TorqueBarycenterReceived += (_, t) => _log($"{tok}: torque-barycenter {t.AngleDegrees:F1}deg");
        }
        return result;
    }

    private static void Apply(TrackedDeviceEntry x, AntPlusTelemetryUpdate u)
    {
        if (u.HeartRate is { } hr) x.HeartRate = hr;
        if (u.PowerWatts is { } pw) x.PowerWatts = pw;
        if (u.AveragePower is { } avg) x.AveragePower = avg;
        if (u.Cadence is { } cad) x.Cadence = cad;
        if (u.SpeedMps is { } sp) x.SpeedMps = sp;
        if (u.TrainerStatus is { } ts) x.TrainerStatus = ts;
        if (u.RrIntervalMs is { } rr) x.RrIntervalMs = rr;
        if (u.LeftTorqueEffectivenessPercent is { } lte) x.LeftTorqueEffectivenessPercent = lte;
        if (u.RightTorqueEffectivenessPercent is { } rte) x.RightTorqueEffectivenessPercent = rte;
        if (u.LeftPedalSmoothnessPercent is { } lps) x.LeftPedalSmoothnessPercent = lps;
        if (u.RightPedalSmoothnessPercent is { } rps) x.RightPedalSmoothnessPercent = rps;
        if (u.CombinedPedalSmoothnessPercent is { } cps) x.CombinedPedalSmoothnessPercent = cps;
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
