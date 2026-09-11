using System.Threading.Channels;
using Hilke.Ant;
using Hilke.Ant.Model;
using Hilke.Ant.Protocol;

namespace Hilke.Ant.Plus;

/// <summary>A single decoded ANT+ heart rate reading (common page fields).</summary>
public readonly record struct HeartRateReading(
    byte ComputedHeartRate,
    byte BeatCount,
    ushort BeatEventTime,
    byte PageNumber,
    DateTimeOffset At) : IAntPlusDataPage;

/// <summary>Decoder for the ANT+ HRM common data-page fields.</summary>
public sealed class HeartRatePageDecoder : IDataPageDecoder<HeartRateReading>
{
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
        reading = new HeartRateReading(computed, beatCount, beatEventTime, page, DateTimeOffset.UtcNow);
        return true;
    }
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

    public AntPlusDeviceId DeviceId { get; }
    public byte ChannelNumber => _channel.ChannelNumber;
    public AntPlusChannelState State => _channel.State.ToPlus();
    public event EventHandler<AntPlusChannelStateChangedEventArgs>? StateChanged;
    public event EventHandler<AntPlusTelemetryUpdate>? TelemetryUpdated;

    private void OnChannelStateChanged(object? sender, ChannelStateChangedEventArgs e) =>
        StateChanged?.Invoke(this, new AntPlusChannelStateChangedEventArgs(e.OldState.ToPlus(), e.NewState.ToPlus()));

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
                    update = new AntPlusTelemetryUpdate { HeartRate = reading.ComputedHeartRate };
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
