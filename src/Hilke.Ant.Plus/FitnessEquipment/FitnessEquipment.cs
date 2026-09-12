using System.Threading.Channels;
using Hilke.Ant;
using Hilke.Ant.Model;
using Hilke.Ant.Protocol;
using Hilke.Ant.Plus.Common;

namespace Hilke.Ant.Plus.FitnessEquipment;

/// <summary>The FE-C page kind surfaced in a <see cref="FitnessEquipmentUpdate"/>.</summary>
public enum FitnessEquipmentPage : byte
{
    /// <summary>General FE Data page (0x10).</summary>
    GeneralData = 0x10,
    /// <summary>Specific Trainer Data page (0x19).</summary>
    SpecificTrainerData = 0x19,
}

/// <summary>FE state reported in the high nibble of the general/trainer flags byte.</summary>
public enum FitnessEquipmentState : byte
{
    /// <summary>Reserved/unknown state.</summary>
    Reserved = 0,
    /// <summary>The equipment is asleep or powered off.</summary>
    AsleepOff = 1,
    /// <summary>The equipment is ready but not yet in use.</summary>
    Ready = 2,
    /// <summary>The equipment is actively in use.</summary>
    InUse = 3,
    /// <summary>The equipment has finished/paused a session.</summary>
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
internal sealed class GeneralFitnessDataDecoder : IDataPageDecoder<GeneralFitnessData>
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
internal sealed class TrainerDataDecoder : IDataPageDecoder<TrainerData>
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
/// Acceptance status of the last control command the FE received, from the Command Status page
/// (0x47). This is the FE's own application-level acknowledgement — distinct from (and more
/// meaningful than) the ANT radio-level acknowledged-transfer completion, which only confirms the
/// bytes reached the device, not that the FE validated or applied them. Not every FE implements
/// this page; if none arrives, treat the command as unconfirmed rather than assuming success.
/// </summary>
public enum FitnessEquipmentCommandStatus : byte
{
    /// <summary>The command was accepted and applied.</summary>
    Pass = 0,
    /// <summary>The command failed.</summary>
    Fail = 1,
    /// <summary>The FE does not support this command.</summary>
    NotSupported = 2,
    /// <summary>The command was rejected (e.g. out of range, wrong mode/sequence).</summary>
    Rejected = 3,
    /// <summary>The command is still being processed.</summary>
    Pending = 4,
    /// <summary>No command has been received yet, or the status is not otherwise known.</summary>
    Uninitialized = 0xFF,
}

/// <summary>ANT+ FE-C Command Status page (0x47): the FE's acknowledgement of the last control command it received.</summary>
public readonly record struct FitnessEquipmentCommandResult(
    byte LastReceivedCommandId,
    byte SequenceNumber,
    FitnessEquipmentCommandStatus Status,
    DateTimeOffset At);

/// <summary>Decoder for the FE-C Command Status page (0x47).</summary>
internal sealed class CommandStatusDecoder : IDataPageDecoder<FitnessEquipmentCommandResult>
{
    public const byte Page = 0x47;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out FitnessEquipmentCommandResult reading)
    {
        reading = default;
        if (payload8.Length < 8 || (payload8[0] & 0x7F) != Page)
            return false;

        reading = new FitnessEquipmentCommandResult(
            payload8[1],
            payload8[2],
            (FitnessEquipmentCommandStatus)payload8[3],
            DateTimeOffset.UtcNow);
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

    /// <summary>FE-C Basic Resistance control page number (0x30); also the value <see cref="FitnessEquipmentCommandResult.LastReceivedCommandId"/> echoes back for it.</summary>
    public const byte BasicResistancePage = 0x30;
    /// <summary>FE-C Target Power control page number (0x31); also the value <see cref="FitnessEquipmentCommandResult.LastReceivedCommandId"/> echoes back for it.</summary>
    public const byte TargetPowerPage = 0x31;

    private readonly AntChannel _channel;
    private readonly GeneralFitnessDataDecoder _general = new();
    private readonly TrainerDataDecoder _trainer = new();
    private readonly CommandStatusDecoder _commandStatus = new();
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

    /// <summary>Raised for each decoded FE-C General FE Data page.</summary>
    public event EventHandler<GeneralFitnessData>? GeneralDataReceived;
    /// <summary>Raised for each decoded FE-C Specific Trainer Data page.</summary>
    public event EventHandler<TrainerData>? TrainerDataReceived;
    /// <summary>
    /// Raised for each decoded FE-C Command Status page (0x47): the FE's own confirmation of the
    /// last control command it received. Not every FE sends this page — see
    /// <see cref="FitnessEquipmentCommandStatus"/>.
    /// </summary>
    public event EventHandler<FitnessEquipmentCommandResult>? CommandStatusReceived;

    /// <summary>The connected device's identity.</summary>
    public AntPlusDeviceId DeviceId { get; }
    /// <summary>The ANT channel number assigned to this connection.</summary>
    public byte ChannelNumber => _channel.ChannelNumber;
    /// <summary>The connection's current channel lifecycle state.</summary>
    public AntPlusChannelState State => _channel.State.ToPlus();
    /// <summary>Raised on every channel lifecycle state transition.</summary>
    public event EventHandler<AntPlusChannelStateChangedEventArgs>? StateChanged;
    /// <summary>Raised for every decoded telemetry update (speed/power/heart rate plus any common pages).</summary>
    public event EventHandler<AntPlusTelemetryUpdate>? TelemetryUpdated;
    /// <summary>Raised for each received page that no decoder (FE-C-specific or common) recognized.</summary>
    public event EventHandler<RawDataPage>? UnrecognizedPageReceived;

    private void OnChannelStateChanged(object? sender, ChannelStateChangedEventArgs e) =>
        StateChanged?.Invoke(this, new AntPlusChannelStateChangedEventArgs(e.OldState.ToPlus(), e.NewState.ToPlus(), e.Reason.ToPlus()));

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
                bool recognized = false;
                if (_general.TryDecode(span, out var general))
                {
                    recognized = true;
                    GeneralDataReceived?.Invoke(this, general);
                    _readings.Writer.TryWrite(new FitnessEquipmentUpdate(FitnessEquipmentPage.GeneralData, general, null));
                    update = new AntPlusTelemetryUpdate { SpeedMps = general.SpeedMetersPerSecond };
                    if (general.HeartRate is { } fhr)
                        update = update with { HeartRate = fhr };
                }
                else if (_trainer.TryDecode(span, out var trainer))
                {
                    recognized = true;
                    TrainerDataReceived?.Invoke(this, trainer);
                    _readings.Writer.TryWrite(new FitnessEquipmentUpdate(FitnessEquipmentPage.SpecificTrainerData, null, trainer));
                    update = new AntPlusTelemetryUpdate { Cadence = trainer.Cadence, TrainerStatus = $"0x{trainer.TrainerStatus:X1}" };
                    if (trainer.InstantaneousPower is { } ip)
                        update = update with { PowerWatts = ip };
                }
                if (_commandStatus.TryDecode(span, out var commandStatus))
                {
                    recognized = true;
                    CommandStatusReceived?.Invoke(this, commandStatus);
                }
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
