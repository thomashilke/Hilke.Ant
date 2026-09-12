namespace Hilke.Ant.Plus.Common;

/// <summary>A decoded ANT+ data page (page number is the low 7 bits of payload byte 0).</summary>
internal interface IAntPlusDataPage
{
    byte PageNumber { get; }
}

/// <summary>Decodes an 8-byte ANT+ payload into a strongly-typed reading.</summary>
internal interface IDataPageDecoder<TReading>
{
    bool TryDecode(ReadOnlySpan<byte> payload8, out TReading reading);
}

/// <summary>
/// A received page that no decoder in this library recognized (profile-specific or common):
/// the raw page number and full 8-byte payload, verbatim. Surfaced via each profile connection's
/// <c>UnrecognizedPageReceived</c> event to diagnose sensors that send pages this library doesn't
/// yet decode - e.g. a manufacturer's proprietary pages, or a spec page not yet implemented here.
/// </summary>
public readonly record struct RawDataPage(byte PageNumber, byte[] Payload, DateTimeOffset At);
