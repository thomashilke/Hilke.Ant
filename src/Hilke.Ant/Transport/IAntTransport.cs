namespace Hilke.Ant.Transport;

/// <summary>
/// A raw duplex byte transport for ANT frames. Deliberately frame-agnostic: framing/checksum
/// live in the core, so concrete transports (serial, in-memory, ...) stay trivial.
/// </summary>
public interface IAntTransport : IAsyncDisposable
{
    bool IsOpen { get; }

    ValueTask OpenAsync(CancellationToken ct = default);

    ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);

    /// <summary>Read bytes into <paramref name="buffer"/>. Returns 0 when the transport is closed.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

    ValueTask CloseAsync(CancellationToken ct = default);
}
