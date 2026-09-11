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
public sealed class HeartRateMonitor : IAsyncDisposable
{
    /// <summary>ANT+ HRM device type.</summary>
    public const byte DeviceType = 120;

    /// <summary>ANT+ HRM main channel period (1/32768 s counts).</summary>
    public const ushort ChannelPeriod = 8070;

    /// <summary>ANT+ managed network number.</summary>
    public const byte AntPlusNetwork = 1;

    private readonly AntChannel _channel;
    private readonly HeartRatePageDecoder _decoder = new();

    public HeartRateMonitor(AntChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    /// <summary>The wrapped channel (for lifecycle: OpenAsync/CloseAsync/state).</summary>
    public AntChannel Channel => _channel;

    /// <summary>Raised for each decoded heart rate reading.</summary>
    public event EventHandler<HeartRateReading>? HeartRateChanged;

    /// <summary>Default HRM slave channel configuration (device type 120, ANT+ freq/period).</summary>
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

    /// <summary>Decode and stream readings from the underlying channel.</summary>
    public async IAsyncEnumerable<HeartRateReading> ReadingsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var message in _channel.ReceiveAsync(ct).ConfigureAwait(false))
        {
            if (_decoder.TryDecode(message.Payload.Span, out var reading))
            {
                HeartRateChanged?.Invoke(this, reading);
                yield return reading;
            }
        }
    }

    public ValueTask DisposeAsync() => _channel.DisposeAsync();
}
