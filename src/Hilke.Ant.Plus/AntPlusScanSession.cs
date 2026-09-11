using System.Runtime.CompilerServices;

namespace Hilke.Ant.Plus;

/// <summary>One discovered device sighting during a continuous scan, with its decoded telemetry.</summary>
public sealed record AntPlusDeviceSighting(AntPlusDeviceId DeviceId, string ProfileName, sbyte? Rssi, DateTimeOffset At, AntPlusTelemetryUpdate Telemetry);

/// <summary>
/// Wraps a core continuous-scan session: decodes every discovered device's broadcasts using a
/// per-device stateful decoder set (some profile decoders, e.g. <see cref="BicyclePowerDecoder"/>,
/// carry state across messages), keyed by <see cref="AntPlusDeviceId"/>.
/// </summary>
public sealed class AntPlusScanSession : IAsyncDisposable
{
    private readonly ScanSession _scan;
    private readonly Dictionary<AntPlusDeviceId, ProfileDecoderSet> _decoders = new();

    internal AntPlusScanSession(ScanSession scan) => _scan = scan;

    public async IAsyncEnumerable<AntPlusDeviceSighting> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var m in _scan.ReceiveAsync(ct).ConfigureAwait(false))
        {
            var id = AntPlusDeviceId.FromCore(m.Device);
            if (!_decoders.TryGetValue(id, out var decoders))
                _decoders[id] = decoders = ProfileDecoderSet.Create(id.DeviceType);
            yield return new AntPlusDeviceSighting(id, AntPlusDeviceCatalog.ProfileName(id.DeviceType), m.Rssi, DateTimeOffset.UtcNow, decoders.Decode(m.Payload.Span));
        }
    }

    public Task StopAsync(CancellationToken ct = default) => _scan.StopAsync(ct);
    public ValueTask DisposeAsync() => _scan.DisposeAsync();

    /// <summary>Per-device stateful decoder set + telemetry dispatch, keyed by device type. Private:
    /// connected monitors know their own type and don't need runtime dispatch (Phase 4).</summary>
    private sealed class ProfileDecoderSet
    {
        private readonly HeartRatePageDecoder? _hrm;
        private readonly BicyclePowerDecoder? _power;
        private readonly GeneralFitnessDataDecoder? _feGeneral;
        private readonly TrainerDataDecoder? _feTrainer;

        private ProfileDecoderSet(HeartRatePageDecoder? hrm, BicyclePowerDecoder? power, GeneralFitnessDataDecoder? feGeneral, TrainerDataDecoder? feTrainer)
        { _hrm = hrm; _power = power; _feGeneral = feGeneral; _feTrainer = feTrainer; }

        public static ProfileDecoderSet Create(byte deviceType) => deviceType switch
        {
            HeartRateMonitor.DeviceType => new(new HeartRatePageDecoder(), null, null, null),
            BicyclePowerMonitor.DeviceType => new(null, new BicyclePowerDecoder(), null, null),
            FitnessEquipmentMonitor.DeviceType => new(null, null, new GeneralFitnessDataDecoder(), new TrainerDataDecoder()),
            _ => new(null, null, null, null),
        };

        public AntPlusTelemetryUpdate Decode(ReadOnlySpan<byte> page8)
        {
            var update = new AntPlusTelemetryUpdate();
            if (_hrm is { } hrm && hrm.TryDecode(page8, out var hr)) update = update with { HeartRate = hr.ComputedHeartRate };
            if (_power is { } power && power.TryDecode(page8, out var pw))
                update = update with { PowerWatts = pw.InstantaneousPower, Cadence = pw.Cadence, AveragePower = pw.AveragePower ?? update.AveragePower };
            if (_feGeneral is { } fg && fg.TryDecode(page8, out var general))
                update = update with { SpeedMps = general.SpeedMetersPerSecond, HeartRate = general.HeartRate ?? update.HeartRate };
            else if (_feTrainer is { } ft && ft.TryDecode(page8, out var trainer))
                update = update with { PowerWatts = trainer.InstantaneousPower ?? update.PowerWatts, Cadence = trainer.Cadence, TrainerStatus = $"0x{trainer.TrainerStatus:X1}" };
            CommonDataPageDecoders.TryDispatch(page8,
                b => update = update with { Battery = b.Status, BatteryVolts = b.Voltage },
                m => update = update with { Manufacturer = m },
                p => update = update with { Product = p });
            return update;
        }
    }
}
