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
