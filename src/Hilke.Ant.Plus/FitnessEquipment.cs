using System.Threading.Channels;
using Hilke.Ant;
using Hilke.Ant.Model;
using Hilke.Ant.Protocol;

namespace Hilke.Ant.Plus;

/// <summary>The FE-C page kind surfaced in a <see cref="FitnessEquipmentUpdate"/>.</summary>
public enum FitnessEquipmentPage : byte
{
    GeneralData = 0x10,
    SpecificTrainerData = 0x19,
}

/// <summary>FE state reported in the high nibble of the general/trainer flags byte.</summary>
public enum FitnessEquipmentState : byte
{
    Reserved = 0,
    AsleepOff = 1,
    Ready = 2,
    InUse = 3,
    Finished = 4,
}

/// <summary>ANT+ FE-C General FE Data page (0x10).</summary>
public readonly record struct GeneralFitnessData(
    byte EquipmentType,
    TimeSpan ElapsedTime,
    byte DistanceMeters,
    double? SpeedMetersPerSecond,
    byte? HeartRate,
    FitnessEquipmentState State,
    byte Capabilities,
    DateTimeOffset At);

/// <summary>ANT+ FE-C Specific Trainer Data page (0x19).</summary>
public readonly record struct TrainerData(
    byte EventCount,
    byte? Cadence,
    ushort AccumulatedPower,
    ushort? InstantaneousPower,
    byte TrainerStatus,
    FitnessEquipmentState State,
    DateTimeOffset At);

/// <summary>One decoded FE-C update: exactly one of <see cref="General"/> / <see cref="Trainer"/> is set.</summary>
public readonly record struct FitnessEquipmentUpdate(
    FitnessEquipmentPage PageType,
    GeneralFitnessData? General,
    TrainerData? Trainer);

/// <summary>Decoder for the FE-C General FE Data page (0x10).</summary>
public sealed class GeneralFitnessDataDecoder : IDataPageDecoder<GeneralFitnessData>
{
    public const byte Page = 0x10;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out GeneralFitnessData reading)
    {
        reading = default;
        if (payload8.Length < 8 || (payload8[0] & 0x7F) != Page)
            return false;

        byte equipmentType = payload8[1];
        var elapsed = TimeSpan.FromSeconds(payload8[2] * 0.25);
        byte distance = payload8[3];
        ushort speedRaw = (ushort)(payload8[4] | (payload8[5] << 8));
        double? speed = speedRaw == 0xFFFF ? null : speedRaw / 1000.0; // mm/s -> m/s
        byte hrRaw = payload8[6];
        byte? hr = hrRaw == 0xFF ? null : hrRaw;
        byte flags = payload8[7];
        var state = (FitnessEquipmentState)((flags >> 4) & 0x07);
        byte capabilities = (byte)(flags & 0x0F);

        reading = new GeneralFitnessData(equipmentType, elapsed, distance, speed, hr, state, capabilities, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>Decoder for the FE-C Specific Trainer Data page (0x19).</summary>
public sealed class TrainerDataDecoder : IDataPageDecoder<TrainerData>
{
    public const byte Page = 0x19;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out TrainerData reading)
    {
        reading = default;
        if (payload8.Length < 8 || (payload8[0] & 0x7F) != Page)
            return false;

        byte eventCount = payload8[1];
        byte cadenceRaw = payload8[2];
        byte? cadence = cadenceRaw == 0xFF ? null : cadenceRaw;
        ushort accumulated = (ushort)(payload8[3] | (payload8[4] << 8));
        int instRaw = payload8[5] | ((payload8[6] & 0x0F) << 8); // 12-bit
        ushort? instantaneous = instRaw == 0x0FFF ? null : (ushort)instRaw;
        byte trainerStatus = (byte)((payload8[6] >> 4) & 0x0F);
        var state = (FitnessEquipmentState)((payload8[7] >> 4) & 0x07);

        reading = new TrainerData(eventCount, cadence, accumulated, instantaneous, trainerStatus, state, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>
/// Reference ANT+ profile: wraps an <see cref="AntChannel"/> as an FE-C controller (slave that
/// also transmits control pages), decodes general + trainer pages, and can command the trainer.
/// </summary>
public sealed class FitnessEquipmentMonitor : IAntPlusProfileConnection
{
    /// <summary>ANT+ FE-C device type.</summary>
    public const byte DeviceType = 17;

    /// <summary>ANT+ FE-C main channel period (1/32768 s counts, 4 Hz).</summary>
    public const ushort ChannelPeriod = 8192;

    /// <summary>ANT+ managed network number.</summary>
    public const byte AntPlusNetwork = AntPlusProtocol.NetworkNumber;

    private const byte BasicResistancePage = 0x30;
    private const byte TargetPowerPage = 0x31;

    private readonly AntChannel _channel;
    private readonly GeneralFitnessDataDecoder _general = new();
    private readonly TrainerDataDecoder _trainer = new();
    private readonly Channel<FitnessEquipmentUpdate> _readings = System.Threading.Channels.Channel.CreateBounded<FitnessEquipmentUpdate>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true });
    private readonly CancellationTokenSource _pumpCts;
    private readonly Task _pump;

    internal FitnessEquipmentMonitor(AntChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        DeviceId = AntPlusDeviceId.FromCore(channel.Configuration.ChannelId);
        _channel.StateChanged += OnChannelStateChanged;
        _pumpCts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    public event EventHandler<GeneralFitnessData>? GeneralDataReceived;
    public event EventHandler<TrainerData>? TrainerDataReceived;

    public AntPlusDeviceId DeviceId { get; }
    public byte ChannelNumber => _channel.ChannelNumber;
    public AntPlusChannelState State => _channel.State.ToPlus();
    public event EventHandler<AntPlusChannelStateChangedEventArgs>? StateChanged;
    public event EventHandler<AntPlusTelemetryUpdate>? TelemetryUpdated;

    private void OnChannelStateChanged(object? sender, ChannelStateChangedEventArgs e) =>
        StateChanged?.Invoke(this, new AntPlusChannelStateChangedEventArgs(e.OldState.ToPlus(), e.NewState.ToPlus()));

    /// <summary>Default FE-C controller (slave) channel configuration.</summary>
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

    /// <summary>Decode and stream FE-C updates (general + trainer pages) from the channel.</summary>
    public IAsyncEnumerable<FitnessEquipmentUpdate> ReadingsAsync(CancellationToken ct = default)
        => _readings.Reader.ReadAllAsync(ct);

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _channel.ReceiveAsync(ct).ConfigureAwait(false))
            {
                var span = message.Payload.Span;
                AntPlusTelemetryUpdate? update = null;
                if (_general.TryDecode(span, out var general))
                {
                    GeneralDataReceived?.Invoke(this, general);
                    _readings.Writer.TryWrite(new FitnessEquipmentUpdate(FitnessEquipmentPage.GeneralData, general, null));
                    update = new AntPlusTelemetryUpdate { SpeedMps = general.SpeedMetersPerSecond };
                    if (general.HeartRate is { } fhr)
                        update = update with { HeartRate = fhr };
                }
                else if (_trainer.TryDecode(span, out var trainer))
                {
                    TrainerDataReceived?.Invoke(this, trainer);
                    _readings.Writer.TryWrite(new FitnessEquipmentUpdate(FitnessEquipmentPage.SpecificTrainerData, null, trainer));
                    update = new AntPlusTelemetryUpdate { Cadence = trainer.Cadence, TrainerStatus = $"0x{trainer.TrainerStatus:X1}" };
                    if (trainer.InstantaneousPower is { } ip)
                        update = update with { PowerWatts = ip };
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

    /// <summary>Encode the FE-C Target Power control page (0x31); target in whole watts.</summary>
    public static byte[] BuildTargetPowerPage(ushort watts)
    {
        ushort units = (ushort)Math.Min(watts * 4, ushort.MaxValue); // 0.25 W units
        return new byte[] { TargetPowerPage, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, (byte)(units & 0xFF), (byte)(units >> 8) };
    }

    /// <summary>Encode the FE-C Basic Resistance control page (0x30); resistance percent 0..100.</summary>
    public static byte[] BuildBasicResistancePage(double percent)
    {
        byte units = (byte)Math.Clamp((int)Math.Round(percent * 2), 0, 200); // 0.5 % units
        return new byte[] { BasicResistancePage, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, units };
    }

    /// <summary>Command the trainer to hold a target power (watts) via an acknowledged control page.</summary>
    public Task SetTargetPowerAsync(ushort watts, CancellationToken ct = default)
        => _channel.SendAcknowledgedAsync(BuildTargetPowerPage(watts), ct);

    /// <summary>Command the trainer's basic resistance (0..100 %) via an acknowledged control page.</summary>
    public Task SetBasicResistanceAsync(double percent, CancellationToken ct = default)
        => _channel.SendAcknowledgedAsync(BuildBasicResistancePage(percent), ct);

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
