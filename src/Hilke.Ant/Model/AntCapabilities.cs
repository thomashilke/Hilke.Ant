namespace Hilke.Ant.Model;

/// <summary>Device capabilities reported by the Capabilities (0x54) message.</summary>
internal sealed record AntCapabilities(
    byte MaxChannels,
    byte MaxNetworks,
    byte StandardOptions,
    byte AdvancedOptions)
{
    /// <summary>A conservative default used before capabilities are known.</summary>
    public static AntCapabilities Unknown { get; } = new(0, 0, 0, 0);
}
