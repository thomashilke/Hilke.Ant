using System.Threading.Channels;
using Hilke.Ant.Transport;

namespace Hilke.Ant.Testing;

/// <summary>
/// A duplex in-memory transport: host writes go to the simulator; simulator frames return to
/// the host's <see cref="ReadAsync"/>. Pair with <see cref="SimulatedAntRadio"/> for tests.
/// </summary>
public sealed class InMemoryAntTransport : IAntTransport
{
    private readonly Channel<byte[]> _toSim = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<byte[]> _toHost = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private bool _open;

    /// <summary>Whether the transport currently has an open connection to the device.</summary>
    public bool IsOpen => _open;

    /// <summary>Open the transport.</summary>
    public ValueTask OpenAsync(CancellationToken ct = default)
    {
        _open = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Write one complete, already-framed ANT message; the simulator will observe it.</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        _toSim.Writer.TryWrite(frame.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>Read bytes previously sent by the simulator into <paramref name="buffer"/>. Returns 0 when closed.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            byte[] frame = await _toHost.Reader.ReadAsync(ct).ConfigureAwait(false);
            if (frame.Length > buffer.Length)
                throw new InvalidOperationException($"Read buffer too small: frame {frame.Length} > buffer {buffer.Length}.");
            frame.CopyTo(buffer);
            return frame.Length;
        }
        catch (ChannelClosedException)
        {
            return 0;
        }
    }

    /// <summary>Close the transport.</summary>
    public ValueTask CloseAsync(CancellationToken ct = default)
    {
        _open = false;
        _toHost.Writer.TryComplete();
        _toSim.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    // ----- simulator side -----

    /// <summary>Frames written by the host, for the simulator to consume.</summary>
    internal ChannelReader<byte[]> HostFrames => _toSim.Reader;

    /// <summary>Enqueue a frame to be delivered to the host's read loop.</summary>
    internal void SendToHost(byte[] frame) => _toHost.Writer.TryWrite(frame);

    /// <summary>Close the transport and release its queues.</summary>
    public ValueTask DisposeAsync()
    {
        _open = false;
        _toHost.Writer.TryComplete();
        _toSim.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
