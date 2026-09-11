namespace Hilke.Ant.Plus;

/// <summary>Resolves an ANT+ device-type byte to a display name.</summary>
public static class AntPlusDeviceCatalog
{
    public static string ProfileName(byte deviceType) => deviceType switch
    {
        HeartRateMonitor.DeviceType => "HRM",
        BicyclePowerMonitor.DeviceType => "Power",
        FitnessEquipmentMonitor.DeviceType => "FE-C",
        _ => $"type {deviceType}",
    };
}
