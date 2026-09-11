namespace Hilke.Ant.Model;

/// <summary>
/// The ANT channel identity that distinguishes a device pairing:
/// device number, device type and transmission type.
/// </summary>
public readonly record struct ChannelId(ushort DeviceNumber, byte DeviceType, byte TransmissionType)
{
    /// <summary>A wildcard id (matches any device) optionally constrained to a device type.</summary>
    public static ChannelId Wildcard(byte deviceType = 0) => new(0, deviceType, 0);

    /// <summary>True when every field is a wildcard (0).</summary>
    public bool IsWildcard => DeviceNumber == 0 && DeviceType == 0 && TransmissionType == 0;
}
