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

    private readonly AntChannel _channel;
    private readonly BicyclePowerDecoder _decoder = new();
    private readonly PowerMeterCalibrationSession _calibration;
    private readonly Channel<BicyclePowerReading> _readings = System.Threading.Channels.Channel.CreateBounded<BicyclePowerReading>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true });
    private readonly CancellationTokenSource _pumpCts;
    private readonly Task _pump;

    internal BicyclePowerMonitor(AntChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _calibration = new PowerMeterCalibrationSession(_channel);
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
                if (_decoder.TryDecode(span, out var reading))
                {
                    PowerReceived?.Invoke(this, reading);
                    _readings.Writer.TryWrite(reading);
                    update = new AntPlusTelemetryUpdate
                    {
                        PowerWatts = reading.InstantaneousPower,
                        Cadence = reading.Cadence,
                        AveragePower = reading.AveragePower,
                    };
                }
                CommonDataPageDecoders.TryDispatch(span,
                    b => update = (update ?? new AntPlusTelemetryUpdate()) with { Battery = b.Status, BatteryVolts = b.Voltage },
                    m => update = (update ?? new AntPlusTelemetryUpdate()) with { Manufacturer = m },
                    p => update = (update ?? new AntPlusTelemetryUpdate()) with { Product = p });
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
