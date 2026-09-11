namespace Hilke.Ant.Transport;

/// <summary>
/// A raw duplex byte transport for ANT frames. Deliberately frame-agnostic: framing/checksum
/// live in the core, so concrete transports (serial, in-memory, ...) stay trivial.
/// </summary>
public interface IAntTransport : IAsyncDisposable
{
    /// <summary>Whether the transport currently has an open connection to the device.</summary>
    bool IsOpen { get; }

    /// <summary>Open the transport (e.g. open the serial port).</summary>
    ValueTask OpenAsync(CancellationToken ct = default);

    /// <summary>Write one complete, already-framed ANT message to the device.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);

    /// <summary>Read bytes into <paramref name="buffer"/>. Returns 0 when the transport is closed.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>Close the transport.</summary>
    ValueTask CloseAsync(CancellationToken ct = default);
}
