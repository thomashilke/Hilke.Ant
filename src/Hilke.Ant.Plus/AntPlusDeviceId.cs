using Hilke.Ant.Model;

namespace Hilke.Ant.Plus;

/// <summary>ANT+ device identity: device number, device type, transmission type.</summary>
public readonly record struct AntPlusDeviceId(ushort DeviceNumber, byte DeviceType, byte TransmissionType)
{
    /// <summary>A wildcard id (matches any device) optionally constrained to a device type.</summary>
    public static AntPlusDeviceId Wildcard(byte deviceType = 0) => new(0, deviceType, 0);

    internal ChannelId ToCore() => new(DeviceNumber, DeviceType, TransmissionType);

    internal static AntPlusDeviceId FromCore(ChannelId id) => new(id.DeviceNumber, id.DeviceType, id.TransmissionType);
}
