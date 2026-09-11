using Hilke.Ant.Plus.HeartRate;
using Hilke.Ant.Plus.BicyclePower;
using Hilke.Ant.Plus.FitnessEquipment;

namespace Hilke.Ant.Cli;

internal static class DeviceDisplay
{
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
