using System.Threading.Channels;
using Hilke.Ant;
using Hilke.Ant.Model;
using Hilke.Ant.Protocol;
using Hilke.Ant.Plus.Common;

namespace Hilke.Ant.Plus.HeartRate;

/// <summary>A single decoded ANT+ heart rate reading (common page fields).</summary>
public readonly record struct HeartRateReading(
    byte ComputedHeartRate,
    byte BeatCount,
    ushort BeatEventTime,
    int? RrIntervalMs,
    byte PageNumber,
    DateTimeOffset At) : IAntPlusDataPage;

/// <summary>Decoder for the ANT+ HRM common data-page fields.</summary>
internal sealed class HeartRatePageDecoder : IDataPageDecoder<HeartRateReading>
{
    private bool _hasPrevious;
    private byte _prevBeatCount;
    private ushort _prevBeatEventTime;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out HeartRateReading reading)
    {
        if (payload8.Length < 8)
        {
            reading = default;
            return false;
        }
        byte page = (byte)(payload8[0] & 0x7F);
        ushort beatEventTime = (ushort)(payload8[4] | (payload8[5] << 8));
        byte beatCount = payload8[6];
        byte computed = payload8[7];

        int? rr = null;
        if (page == 4 && payload8.Length >= 4)
        {
            ushort previousBeatEventTime = (ushort)(payload8[2] | (payload8[3] << 8));
            rr = ComputeRrMs(beatEventTime, previousBeatEventTime);
        }
        else if (_hasPrevious && (byte)(beatCount - _prevBeatCount) == 1)
        {
            rr = ComputeRrMs(beatEventTime, _prevBeatEventTime);
        }
        _hasPrevious = true;
        _prevBeatCount = beatCount;
        _prevBeatEventTime = beatEventTime;

        reading = new HeartRateReading(computed, beatCount, beatEventTime, rr, page, DateTimeOffset.UtcNow);
        return true;
    }

    private static int ComputeRrMs(ushort current, ushort previous) =>
        (int)((ushort)(current - previous) * 1000L / 1024L);
}

/// <summary>
/// Reference ANT+ profile: wraps an <see cref="AntChannel"/> configured for a heart rate monitor,
/// decodes incoming pages, and surfaces readings via an event and an async stream.
/// </summary>
public sealed class HeartRateMonitor : IAntPlusProfileConnection
{
    /// <summary>ANT+ HRM device type.</summary>
    public const byte DeviceType = 120;

    /// <summary>ANT+ HRM main channel period (1/32768 s counts).</summary>
    public const ushort ChannelPeriod = 8070;

    /// <summary>ANT+ managed network number.</summary>
    public const byte AntPlusNetwork = AntPlusProtocol.NetworkNumber;

    private readonly AntChannel _channel;
    private readonly HeartRatePageDecoder _decoder = new();
    private readonly Channel<HeartRateReading> _readings = System.Threading.Channels.Channel.CreateBounded<HeartRateReading>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true });
    private readonly CancellationTokenSource _pumpCts;
    private readonly Task _pump;

    internal HeartRateMonitor(AntChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        DeviceId = AntPlusDeviceId.FromCore(channel.Configuration.ChannelId);
        _channel.StateChanged += OnChannelStateChanged;
        _pumpCts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    /// <summary>Raised for each decoded heart rate reading.</summary>
    public event EventHandler<HeartRateReading>? HeartRateChanged;

    /// <summary>The connected device's identity.</summary>
    public AntPlusDeviceId DeviceId { get; }
    /// <summary>The ANT channel number assigned to this connection.</summary>
    public byte ChannelNumber => _channel.ChannelNumber;
    /// <summary>The connection's current channel lifecycle state.</summary>
    public AntPlusChannelState State => _channel.State.ToPlus();
    /// <summary>Raised on every channel lifecycle state transition.</summary>
    public event EventHandler<AntPlusChannelStateChangedEventArgs>? StateChanged;
    /// <summary>Raised for every decoded telemetry update (heart rate plus any common pages).</summary>
    public event EventHandler<AntPlusTelemetryUpdate>? TelemetryUpdated;
    /// <summary>Raised for each received page that no decoder (HRM-specific or common) recognized.</summary>
    public event EventHandler<RawDataPage>? UnrecognizedPageReceived;

    private void OnChannelStateChanged(object? sender, ChannelStateChangedEventArgs e) =>
        StateChanged?.Invoke(this, new AntPlusChannelStateChangedEventArgs(e.OldState.ToPlus(), e.NewState.ToPlus(), e.Reason.ToPlus()));

    /// <summary>Default HRM slave channel configuration (device type 120, ANT+ freq/period).</summary>
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

    /// <summary>Decode and stream readings from the underlying channel.</summary>
    public IAsyncEnumerable<HeartRateReading> ReadingsAsync(CancellationToken ct = default)
        => _readings.Reader.ReadAllAsync(ct);

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _channel.ReceiveAsync(ct).ConfigureAwait(false))
            {
                var span = message.Payload.Span;
                AntPlusTelemetryUpdate? update = null;
                if (_decoder.TryDecode(span, out var reading))
                {
                    HeartRateChanged?.Invoke(this, reading);
                    _readings.Writer.TryWrite(reading);
                    update = new AntPlusTelemetryUpdate { HeartRate = reading.ComputedHeartRate, RrIntervalMs = reading.RrIntervalMs };
                }
                bool recognized = CommonDataPageDecoders.TryDispatch(span,
                    b => update = (update ?? new AntPlusTelemetryUpdate()) with { Battery = b.Status, BatteryVolts = b.Voltage },
                    m => update = (update ?? new AntPlusTelemetryUpdate()) with { Manufacturer = m },
                    p => update = (update ?? new AntPlusTelemetryUpdate()) with { Product = p });
                if (!recognized && update is null)
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
