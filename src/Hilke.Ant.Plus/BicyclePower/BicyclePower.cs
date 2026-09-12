using System.Threading.Channels;
using Hilke.Ant;
using Hilke.Ant.Model;
using Hilke.Ant.Protocol;
using Hilke.Ant.Plus.Common;

namespace Hilke.Ant.Plus.BicyclePower;

/// <summary>
/// A decoded ANT+ Bicycle Power standard power-only page (0x10). <see cref="AveragePower"/> is
/// derived across successive messages from the accumulated-power / event-count deltas.
/// </summary>
public readonly record struct BicyclePowerReading(
    byte EventCount,
    ushort InstantaneousPower,
    ushort AccumulatedPower,
    byte? Cadence,
    byte? PedalPowerPercent,
    double? AveragePower,
    byte PageNumber,
    DateTimeOffset At) : IAntPlusDataPage;

/// <summary>
/// Stateful decoder for the ANT+ Bicycle Power "standard power-only" page (0x10). Retains the
/// previous event count / accumulated power to compute average power (watts) across messages.
/// </summary>
internal sealed class BicyclePowerDecoder : IDataPageDecoder<BicyclePowerReading>
{
    /// <summary>Standard power-only data page number.</summary>
    public const byte PowerOnlyPage = 0x10;

    private bool _hasPrevious;
    private byte _prevEventCount;
    private ushort _prevAccumulated;

    /// <summary>Reset the running state used for average-power computation.</summary>
    public void Reset() => _hasPrevious = false;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out BicyclePowerReading reading)
    {
        reading = default;
        if (payload8.Length < 8)
            return false;
        byte page = (byte)(payload8[0] & 0x7F);
        if (page != PowerOnlyPage)
            return false;

        byte eventCount = payload8[1];
        byte pedalRaw = payload8[2];
        byte cadenceRaw = payload8[3];
        ushort accumulated = (ushort)(payload8[4] | (payload8[5] << 8));
        ushort instantaneous = (ushort)(payload8[6] | (payload8[7] << 8));

        byte? cadence = cadenceRaw == 0xFF ? null : cadenceRaw;
        byte? pedal = pedalRaw == 0xFF ? null : (byte)(pedalRaw & 0x7F);

        double? average = null;
        if (_hasPrevious)
        {
            int eventDelta = (eventCount - _prevEventCount) & 0xFF;
            int accumDelta = (accumulated - _prevAccumulated) & 0xFFFF;
            if (eventDelta > 0)
                average = (double)accumDelta / eventDelta;
        }
        _prevEventCount = eventCount;
        _prevAccumulated = accumulated;
        _hasPrevious = true;

        reading = new BicyclePowerReading(eventCount, instantaneous, accumulated, cadence, pedal, average, page, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>A decoded ANT+ Torque Effectiveness and Pedal Smoothness page (0x13).</summary>
public readonly record struct TorqueEffectivenessReading(
    byte EventCount,
    double? LeftTorqueEffectivenessPercent,
    double? RightTorqueEffectivenessPercent,
    double? LeftPedalSmoothnessPercent,
    double? RightPedalSmoothnessPercent,
    double? CombinedPedalSmoothnessPercent,
    byte PageNumber,
    DateTimeOffset At) : IAntPlusDataPage;

/// <summary>Decoder for the ANT+ Bicycle Power Torque Effectiveness and Pedal Smoothness page (0x13).</summary>
internal sealed class TorqueEffectivenessDecoder : IDataPageDecoder<TorqueEffectivenessReading>
{
    public const byte Page = 0x13;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out TorqueEffectivenessReading reading)
    {
        reading = default;
        if (payload8.Length < 8 || (payload8[0] & 0x7F) != Page)
            return false;

        byte eventCount = payload8[1];
        double? Pct(byte raw) => raw == 0xFF ? null : raw * 0.5;
        double? leftTe = Pct(payload8[2]);
        double? rightTe = Pct(payload8[3]);
        double? leftOrCombinedPs = Pct(payload8[4]);
        byte rightPsRaw = payload8[5];

        double? leftPs, rightPs, combinedPs;
        if (rightPsRaw == 0xFE) { combinedPs = leftOrCombinedPs; leftPs = null; rightPs = null; }
        else { leftPs = leftOrCombinedPs; rightPs = Pct(rightPsRaw); combinedPs = null; }

        reading = new TorqueEffectivenessReading(eventCount, leftTe, rightTe, leftPs, rightPs, combinedPs, Page, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>
/// Reference ANT+ profile: wraps an <see cref="AntChannel"/> configured as a Bicycle Power
/// display (slave), decodes standard power-only pages, and surfaces readings.
/// </summary>
public sealed class BicyclePowerMonitor : IAntPlusProfileConnection
{
    /// <summary>ANT+ Bicycle Power device type.</summary>
    public const byte DeviceType = 11;

    /// <summary>ANT+ Bicycle Power main channel period (1/32768 s counts, ~4.005 Hz).</summary>
    public const ushort ChannelPeriod = 8182;

    /// <summary>ANT+ managed network number.</summary>
    public const byte AntPlusNetwork = AntPlusProtocol.NetworkNumber;

    private AntChannel _channel;
    private readonly BicyclePowerDecoder _decoder = new();
    private readonly TorqueEffectivenessDecoder _tePs = new();
    private readonly PedalForceAngleDecoder _forceAngle = new();
    private readonly PedalPositionDecoder _pedalPosition = new();
    private readonly TorqueBarycenterDecoder _torqueBarycenter = new();
    private readonly PowerMeterCalibrationSession _calibration;
    private CyclingDynamicsCapabilityQuery _cyclingDynamics;
    private Channel<BicyclePowerReading> _readings = System.Threading.Channels.Channel.CreateBounded<BicyclePowerReading>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true });
    private CancellationTokenSource _pumpCts;
    private Task _pump;

    internal BicyclePowerMonitor(AntChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _calibration = new PowerMeterCalibrationSession(_channel);
        _cyclingDynamics = new CyclingDynamicsCapabilityQuery(_channel);
        DeviceId = AntPlusDeviceId.FromCore(channel.Configuration.ChannelId);
        _channel.StateChanged += OnChannelStateChanged;
        _pumpCts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    /// <summary>The connected device's identity.</summary>
    public AntPlusDeviceId DeviceId { get; }
    /// <summary>The ANT channel number assigned to this connection.</summary>
    public byte ChannelNumber => _channel.ChannelNumber;
    /// <summary>The connection's current channel lifecycle state.</summary>
    public AntPlusChannelState State => _channel.State.ToPlus();
    /// <summary>Raised on every channel lifecycle state transition.</summary>
    public event EventHandler<AntPlusChannelStateChangedEventArgs>? StateChanged;
    /// <summary>Raised for every decoded telemetry update (power plus any common pages).</summary>
    public event EventHandler<AntPlusTelemetryUpdate>? TelemetryUpdated;

    private void OnChannelStateChanged(object? sender, ChannelStateChangedEventArgs e) =>
        StateChanged?.Invoke(this, new AntPlusChannelStateChangedEventArgs(e.OldState.ToPlus(), e.NewState.ToPlus(), e.Reason.ToPlus()));

    /// <summary>The power-meter calibration session driven by this monitor's read pump.</summary>
    public PowerMeterCalibrationSession Calibration => _calibration;

    /// <summary>Send a manual-zero calibration request and await the sensor's response.</summary>
    public Task<PowerMeterCalibrationResult> RequestManualZeroAsync(TimeSpan timeout, CancellationToken ct = default)
        => _calibration.RequestManualZeroAsync(timeout, ct);

    /// <summary>Configure auto-zero on the sensor and await its response.</summary>
    public Task<PowerMeterCalibrationResult> ConfigureAutoZeroAsync(bool enable, TimeSpan timeout, CancellationToken ct = default)
        => _calibration.ConfigureAutoZeroAsync(enable, timeout, ct);

    /// <summary>Raised for each decoded power reading.</summary>
    public event EventHandler<BicyclePowerReading>? PowerReceived;
    /// <summary>Raised for each decoded Torque Effectiveness and Pedal Smoothness reading.</summary>
    public event EventHandler<TorqueEffectivenessReading>? TorqueEffectivenessReceived;
    /// <summary>Raised for each decoded Pedal Force Angle (Power Phase) reading, once Cycling Dynamics is enabled.</summary>
    public event EventHandler<PedalForceAngleReading>? PedalForceAngleReceived;
    /// <summary>Raised for each decoded Pedal Position reading, once Cycling Dynamics is enabled.</summary>
    public event EventHandler<PedalPositionReading>? PedalPositionReceived;
    /// <summary>Raised for each decoded Torque Barycenter reading, once Cycling Dynamics is enabled.</summary>
    public event EventHandler<TorqueBarycenterReading>? TorqueBarycenterReceived;
    /// <summary>Raised for each received page that no decoder (Bicycle-Power-specific or common) recognized.</summary>
    public event EventHandler<RawDataPage>? UnrecognizedPageReceived;

    /// <summary>Default Bicycle Power display (slave) channel configuration.</summary>
    internal static ChannelConfiguration SlaveDefaults(ChannelId? id = null) => new()
    {
        Type = ChannelType.BidirectionalSlave,
        NetworkNumber = AntPlusNetwork,
        ChannelId = id ?? ChannelId.Wildcard(DeviceType),
        RfFrequency = AntPlusProtocol.RfFrequency,
        ChannelPeriod = ChannelPeriod,
        UseExtendedMessages = true,
        InactivityTimeout = TimeSpan.FromSeconds(4),
    };

    /// <summary>Bicycle Power channel configuration at 8Hz (~8.01 Hz), required for Cycling Dynamics pages.</summary>
    internal static ChannelConfiguration CyclingDynamicsSlaveDefaults(ChannelId id) => SlaveDefaults(id) with { ChannelPeriod = 4091 };

    /// <summary>
    /// Query the sensor's Cycling Dynamics capabilities and, if any of <paramref name="requested"/> are
    /// supported, enable them and switch the channel to 8Hz. Returns <see cref="CyclingDynamicsResult.Unsupported"/>
    /// (no channel change) if the sensor never responds to the capability query or supports none of what
    /// was requested.
    /// </summary>
    public async Task<CyclingDynamicsCapabilities> EnableCyclingDynamicsAsync(CyclingDynamicsFeatures requested, TimeSpan timeout, CancellationToken ct = default)
    {
        var response = await _cyclingDynamics.QueryAsync(timeout, ct).ConfigureAwait(false);
        if (response is not { } resp)
            return new CyclingDynamicsCapabilities(CyclingDynamicsResult.TimedOut, CyclingDynamicsFeatures.None, CyclingDynamicsFeatures.None);

        var supported = (CyclingDynamicsFeatures)(~resp.CapabilitiesMask & 0x78);
        var toEnable = requested & supported;
        if (toEnable == CyclingDynamicsFeatures.None)
            return new CyclingDynamicsCapabilities(CyclingDynamicsResult.Unsupported, supported, CyclingDynamicsFeatures.None);

        try { await _cyclingDynamics.SendSetAsync(toEnable, ct).ConfigureAwait(false); }
        catch (AntCommandException ex) { throw new AntPlusCommandException(ex.Message); }
        catch (AntTimeoutException ex) { throw new AntPlusTimeoutException(ex.Message); }

        await SwitchToEightHertzAsync(ct).ConfigureAwait(false);

        var result = toEnable == requested ? CyclingDynamicsResult.Enabled : CyclingDynamicsResult.PartiallyEnabled;
        return new CyclingDynamicsCapabilities(result, supported, toEnable);
    }

    private async Task SwitchToEightHertzAsync(CancellationToken ct)
    {
        var oldChannel = _channel;
        var device = oldChannel.Device;
        var channelId = oldChannel.Configuration.ChannelId;
        var channelNumber = oldChannel.ChannelNumber;

        _pumpCts.Cancel();
        _readings.Writer.TryComplete();
        try { await _pump.ConfigureAwait(false); } catch { /* pump observes its own cancellation */ }
        _pumpCts.Dispose();
        oldChannel.StateChanged -= OnChannelStateChanged;
        try { await oldChannel.CloseAsync(ct).ConfigureAwait(false); } catch { /* best effort */ }
        try { await oldChannel.UnassignAsync(ct).ConfigureAwait(false); } catch { /* best effort */ }
        await oldChannel.DisposeAsync().ConfigureAwait(false);

        AntChannel newChannel;
        try { newChannel = await device.ConfigureChannelAsync(channelNumber, CyclingDynamicsSlaveDefaults(channelId), ct).ConfigureAwait(false); }
        catch (RadioBusyException ex) { throw new AntPlusBusyException(ex.Message); }
        catch (AntCommandException ex) { throw new AntPlusCommandException(ex.Message); }
        catch (AntTimeoutException ex) { throw new AntPlusTimeoutException(ex.Message); }
        try { await newChannel.OpenAsync(ct).ConfigureAwait(false); }
        catch (AntCommandException ex) { throw new AntPlusCommandException(ex.Message); }
        catch (AntTimeoutException ex) { throw new AntPlusTimeoutException(ex.Message); }
        catch (InvalidChannelStateException ex) { throw new AntPlusCommandException(ex.Message); }

        _channel = newChannel;
        _channel.StateChanged += OnChannelStateChanged;
        _cyclingDynamics = new CyclingDynamicsCapabilityQuery(_channel);
        _readings = System.Threading.Channels.Channel.CreateBounded<BicyclePowerReading>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true });
        _pumpCts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    /// <summary>Decode and stream power readings from the underlying channel.</summary>
    public IAsyncEnumerable<BicyclePowerReading> ReadingsAsync(CancellationToken ct = default)
        => _readings.Reader.ReadAllAsync(ct);

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _channel.ReceiveAsync(ct).ConfigureAwait(false))
            {
                var span = message.Payload.Span;
                _calibration.HandleData(span, message.ReceivedAt);

                AntPlusTelemetryUpdate? update = null;
                bool recognized = false;
                if (_decoder.TryDecode(span, out var reading))
                {
                    recognized = true;
                    PowerReceived?.Invoke(this, reading);
                    _readings.Writer.TryWrite(reading);
                    update = new AntPlusTelemetryUpdate
                    {
                        PowerWatts = reading.InstantaneousPower,
                        Cadence = reading.Cadence,
                        AveragePower = reading.AveragePower,
                    };
                }
                if (_tePs.TryDecode(span, out var tePs))
                {
                    recognized = true;
                    TorqueEffectivenessReceived?.Invoke(this, tePs);
                    update = (update ?? new AntPlusTelemetryUpdate()) with
                    {
                        LeftTorqueEffectivenessPercent = tePs.LeftTorqueEffectivenessPercent,
                        RightTorqueEffectivenessPercent = tePs.RightTorqueEffectivenessPercent,
                        LeftPedalSmoothnessPercent = tePs.LeftPedalSmoothnessPercent,
                        RightPedalSmoothnessPercent = tePs.RightPedalSmoothnessPercent,
                        CombinedPedalSmoothnessPercent = tePs.CombinedPedalSmoothnessPercent,
                    };
                }
                if (_cyclingDynamics.HandleData(span)) recognized = true;
                if (_forceAngle.TryDecode(span, out var fa)) { recognized = true; PedalForceAngleReceived?.Invoke(this, fa); }
                if (_pedalPosition.TryDecode(span, out var pp)) { recognized = true; PedalPositionReceived?.Invoke(this, pp); }
                if (_torqueBarycenter.TryDecode(span, out var tb)) { recognized = true; TorqueBarycenterReceived?.Invoke(this, tb); }
                if (CommonDataPageDecoders.TryDispatch(span,
                    b => update = (update ?? new AntPlusTelemetryUpdate()) with { Battery = b.Status, BatteryVolts = b.Voltage },
                    m => update = (update ?? new AntPlusTelemetryUpdate()) with { Manufacturer = m },
                    p => update = (update ?? new AntPlusTelemetryUpdate()) with { Product = p }))
                    recognized = true;
                if (!recognized)
                    UnrecognizedPageReceived?.Invoke(this, new RawDataPage((byte)(span[0] & 0x7F), span.ToArray(), DateTimeOffset.UtcNow));
                if (update is { } u)
                    TelemetryUpdated?.Invoke(this, u);
            }
        }
        catch (OperationCanceledException) { }
        finally { _readings.Writer.TryComplete(); }
    }

    /// <summary>Stop the background pump and gracefully close/unassign/dispose the underlying channel.</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.StateChanged -= OnChannelStateChanged;
        _pumpCts.Cancel();
        _readings.Writer.TryComplete();
        try { await _pump.ConfigureAwait(false); } catch { /* pump observes its own cancellation */ }
        _pumpCts.Dispose();
        try { await _channel.CloseAsync().ConfigureAwait(false); } catch { /* best effort, mirrors today's AntSession.DisconnectAsync catch */ }
        try { await _channel.UnassignAsync().ConfigureAwait(false); } catch { /* best effort */ }
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
