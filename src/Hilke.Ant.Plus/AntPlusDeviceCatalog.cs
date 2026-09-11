using Hilke.Ant.Plus.HeartRate;
using Hilke.Ant.Plus.BicyclePower;
using Hilke.Ant.Plus.FitnessEquipment;

namespace Hilke.Ant.Plus;

/// <summary>Resolves an ANT+ device-type byte to a display name.</summary>
public static class AntPlusDeviceCatalog
{
    /// <summary>Resolve a display name for a known ANT+ device type; falls back to "type N".</summary>
    public static string ProfileName(byte deviceType) => deviceType switch
    {
        HeartRateMonitor.DeviceType => "HRM",
        BicyclePowerMonitor.DeviceType => "Power",
        FitnessEquipmentMonitor.DeviceType => "FE-C",
        _ => $"type {deviceType}",
    };
}
