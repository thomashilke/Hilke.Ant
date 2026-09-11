namespace Hilke.Ant.Plus;

/// <summary>ANT+ managed-network protocol constants (RF frequency, network number/key).</summary>
internal static class AntPlusProtocol
{
    /// <summary>ANT+ RF frequency value (2457 MHz).</summary>
    internal const byte RfFrequency = 57;

    /// <summary>
    /// The network number this library programs the ANT+ key onto and configures every ANT+
    /// profile channel (and the scan channel) against. Must match across
    /// <see cref="NetworkKey"/> and every channel's network number, or the radio silently
    /// ignores peers on the mismatched network (channel opens fine but nothing is ever received).
    /// </summary>
    internal const byte NetworkNumber = 1;

    /// <summary>
    /// The ANT+ managed-network key (thisisant.com calls it confidential to adopters, but it is
    /// widely published in open-source ANT+ clients, e.g. openant and USBHost_t36). Must be set
    /// on <see cref="NetworkNumber"/> before any ANT+ profile channel can open.
    /// </summary>
    internal static readonly byte[] NetworkKey = { 0xB9, 0xA5, 0x21, 0xFB, 0xBD, 0x72, 0xC3, 0x45 };
}
