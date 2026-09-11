using System.Threading.Channels;
using Hilke.Ant;
using Hilke.Ant.Model;
using Hilke.Ant.Protocol;

namespace Hilke.Ant.Plus;

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
public sealed class BicyclePowerDecoder : IDataPageDecoder<BicyclePowerReading>
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
public sealed class BicyclePowerMonitor : IAsyncDisposable
{
    /// <summary>ANT+ Bicycle Power device type.</summary>
    public const byte DeviceType = 11;

    /// <summary>ANT+ Bicycle Power main channel period (1/32768 s counts, ~4.005 Hz).</summary>
    public const ushort ChannelPeriod = 8182;

    /// <summary>ANT+ managed network number.</summary>
    public const byte AntPlusNetwork = 1;

    private readonly AntChannel _channel;
    private readonly BicyclePowerDecoder _decoder = new();
    private readonly PowerMeterCalibrationSession _calibration;
    private readonly Channel<BicyclePowerReading> _readings = System.Threading.Channels.Channel.CreateBounded<BicyclePowerReading>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true });
    private readonly object _pumpGate = new();
    private Task? _pump;
    private CancellationTokenSource? _pumpCts;

    public BicyclePowerMonitor(AntChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _calibration = new PowerMeterCalibrationSession(_channel);
    }

    public AntChannel Channel => _channel;

    /// <summary>The power-meter calibration session driven by this monitor's read pump.</summary>
    public PowerMeterCalibrationSession Calibration => _calibration;

    /// <summary>Send a manual-zero calibration request and await the sensor's response.</summary>
    public Task<PowerMeterCalibrationResult> RequestManualZeroAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        EnsurePump();
        return _calibration.RequestManualZeroAsync(timeout, ct);
    }

    /// <summary>Configure auto-zero on the sensor and await its response.</summary>
    public Task<PowerMeterCalibrationResult> ConfigureAutoZeroAsync(bool enable, TimeSpan timeout, CancellationToken ct = default)
    {
        EnsurePump();
        return _calibration.ConfigureAutoZeroAsync(enable, timeout, ct);
    }

    /// <summary>Raised for each decoded power reading.</summary>
    public event EventHandler<BicyclePowerReading>? PowerReceived;

    /// <summary>Default Bicycle Power display (slave) channel configuration.</summary>
    public static ChannelConfiguration SlaveDefaults(ChannelId? id = null) => new()
    {
        Type = ChannelType.BidirectionalSlave,
        NetworkNumber = AntPlusNetwork,
        ChannelId = id ?? ChannelId.Wildcard(DeviceType),
        RfFrequency = AntConstants.AntPlusRfFrequency,
        ChannelPeriod = ChannelPeriod,
        UseExtendedMessages = true,
        InactivityTimeout = TimeSpan.FromSeconds(4),
    };

    /// <summary>Decode and stream power readings from the underlying channel.</summary>
    public async IAsyncEnumerable<BicyclePowerReading> ReadingsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsurePump();
        await foreach (var reading in _readings.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return reading;
    }

    private void EnsurePump()
    {
        lock (_pumpGate)
        {
            if (_pump is not null)
                return;
            _pumpCts = new CancellationTokenSource();
            _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _channel.ReceiveAsync(ct).ConfigureAwait(false))
            {
                _calibration.HandleData(message.Payload.Span, message.ReceivedAt);
                if (_decoder.TryDecode(message.Payload.Span, out var reading))
                {
                    PowerReceived?.Invoke(this, reading);
                    _readings.Writer.TryWrite(reading);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { _readings.Writer.TryComplete(); }
    }

    public async ValueTask DisposeAsync()
    {
        _pumpCts?.Cancel();
        _readings.Writer.TryComplete();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch { /* pump cancellation/teardown errors are non-fatal */ }
        }
        _pumpCts?.Dispose();
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
