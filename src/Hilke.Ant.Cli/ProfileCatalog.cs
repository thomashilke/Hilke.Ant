using Hilke.Ant.Model;
using Hilke.Ant.Plus;

namespace Hilke.Ant.Cli;

/// <summary>Per-entry decoder set: stateful profile decoders plus shared common-page decoders.</summary>
public sealed class ProfileDecoders
{
    public HeartRatePageDecoder? Hrm { get; init; }
    public BicyclePowerDecoder? Power { get; init; }
    public GeneralFitnessDataDecoder? FeGeneral { get; init; }
    public TrainerDataDecoder? FeTrainer { get; init; }
}

/// <summary>
/// Maps ANT+ <c>DeviceType</c> to profile name, channel config, decoder set, telemetry update and
/// display formatting. Reuses the library <c>SlaveDefaults</c> and page decoders.
/// </summary>
public static class ProfileCatalog
{
    // Common-page decoders are stateless; Update is always invoked under the registry lock, so a
    // single shared instance of each is safe to reuse across every entry.
    private static readonly BatteryStatusDecoder BatteryDecoder = new();
    private static readonly ManufacturerInfoDecoder ManufacturerDecoder = new();
    private static readonly ProductInfoDecoder ProductDecoder = new();

    public static bool TryGetProfileName(byte deviceType, out string name)
    {
        switch (deviceType)
        {
            case HeartRateMonitor.DeviceType: name = "HRM"; return true;
            case BicyclePowerMonitor.DeviceType: name = "Power"; return true;
            case FitnessEquipmentMonitor.DeviceType: name = "FE-C"; return true;
            default: name = $"type {deviceType}"; return false;
        }
    }

    /// <summary>Profile name for display; falls back to a "type N" label for unknown types.</summary>
    public static string ProfileNameOrUnknown(byte deviceType)
    {
        TryGetProfileName(deviceType, out var name);
        return name;
    }

    public static ChannelConfiguration BuildConfig(byte deviceType, ChannelId id) => deviceType switch
    {
        HeartRateMonitor.DeviceType => HeartRateMonitor.SlaveDefaults(id),
        BicyclePowerMonitor.DeviceType => BicyclePowerMonitor.SlaveDefaults(id),
        FitnessEquipmentMonitor.DeviceType => FitnessEquipmentMonitor.SlaveDefaults(id),
        _ => throw new NotSupportedException($"No ANT+ profile for device type {deviceType}"),
    };

    public static object CreateDecoderSet(byte deviceType) => deviceType switch
    {
        HeartRateMonitor.DeviceType => new ProfileDecoders { Hrm = new HeartRatePageDecoder() },
        BicyclePowerMonitor.DeviceType => new ProfileDecoders { Power = new BicyclePowerDecoder() },
        FitnessEquipmentMonitor.DeviceType => new ProfileDecoders
        {
            FeGeneral = new GeneralFitnessDataDecoder(),
            FeTrainer = new TrainerDataDecoder(),
        },
        _ => new ProfileDecoders(),
    };

    /// <summary>Decode <paramref name="page8"/> into <paramref name="entry"/>'s telemetry fields.</summary>
    public static void Update(TrackedDeviceEntry entry, ReadOnlySpan<byte> page8)
    {
        // Common pages apply to every device regardless of profile.
        if (BatteryDecoder.TryDecode(page8, out var battery))
        {
            entry.Battery = battery.Status;
            entry.BatteryVolts = battery.Voltage;
        }
        if (ManufacturerDecoder.TryDecode(page8, out var mfg))
            entry.Manufacturer = mfg;
        if (ProductDecoder.TryDecode(page8, out var product))
            entry.Product = product;

        if (entry.DecoderState is not ProfileDecoders d)
            return;

        switch (entry.DeviceType)
        {
            case HeartRateMonitor.DeviceType:
                if (d.Hrm is { } hrm && hrm.TryDecode(page8, out var hr))
                    entry.HeartRate = hr.ComputedHeartRate;
                break;

            case BicyclePowerMonitor.DeviceType:
                if (d.Power is { } power && power.TryDecode(page8, out var pw))
                {
                    entry.PowerWatts = pw.InstantaneousPower;
                    entry.Cadence = pw.Cadence;
                    if (pw.AveragePower is { } avg)
                        entry.AveragePower = avg;
                }
                break;

            case FitnessEquipmentMonitor.DeviceType:
                if (d.FeGeneral is { } fg && fg.TryDecode(page8, out var general))
                {
                    entry.SpeedMps = general.SpeedMetersPerSecond;
                    if (general.HeartRate is { } fhr)
                        entry.HeartRate = fhr;
                }
                else if (d.FeTrainer is { } ft && ft.TryDecode(page8, out var trainer))
                {
                    if (trainer.InstantaneousPower is { } ip)
                        entry.PowerWatts = ip;
                    entry.Cadence = trainer.Cadence;
                    entry.TrainerStatus = $"0x{trainer.TrainerStatus:X1}";
                }
                break;
        }
    }

    /// <summary>One-line telemetry summary for the device table.</summary>
    public static string FormatPrimary(TrackedDeviceEntry e) => e.DeviceType switch
    {
        HeartRateMonitor.DeviceType => $"{Fmt(e.HeartRate)} bpm",
        BicyclePowerMonitor.DeviceType => $"{Fmt(e.PowerWatts)} W  {Fmt(e.Cadence)} rpm",
        FitnessEquipmentMonitor.DeviceType => $"{Fmt(e.PowerWatts)} W  {Fmt(e.SpeedMps)} m/s",
        _ => "--",
    };

    private static string Fmt(int? v) => v?.ToString() ?? "--";
    private static string Fmt(double? v) => v is { } d ? d.ToString("F1") : "--";
}
