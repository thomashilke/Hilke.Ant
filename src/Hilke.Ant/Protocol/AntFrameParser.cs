namespace Hilke.Ant.Protocol;

/// <summary>
/// Incremental, resynchronizing frame parser. Feed bytes with <see cref="Append"/> and pull
/// complete messages with <see cref="TryReadMessage"/>. Handles partial frames across appends,
/// scans for SYNC on garbage, and drops frames failing length/checksum by skipping one byte.
/// </summary>
public sealed class AntFrameParser
{
    private byte[] _buffer;
    private int _start; // index of first unconsumed byte
    private int _end;   // index past last valid byte

    public AntFrameParser(int initialCapacity = 256)
    {
        _buffer = new byte[Math.Max(16, initialCapacity)];
    }

    /// <summary>Number of malformed bytes discarded during resynchronization (diagnostics).</summary>
    public long DiscardedBytes { get; private set; }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_end));
        _end += bytes.Length;
    }

    /// <summary>
    /// Try to extract the next complete message. Returns false when more bytes are needed.
    /// A recoverable malformed frame is skipped internally; the method keeps scanning within
    /// the currently buffered bytes before returning false.
    /// </summary>
    public bool TryReadMessage(out AntMessage message)
    {
        while (true)
        {
            int available = _end - _start;
            if (available <= 0)
            {
                Reset();
                message = default;
                return false;
            }

            // Resync to SYNC.
            if (_buffer[_start] != AntConstants.Sync)
            {
                _start++;
                DiscardedBytes++;
                continue;
            }

            // Need at least SYNC + LENGTH.
            if (available < 2)
            {
                message = default;
                return false;
            }

            int length = _buffer[_start + 1];
            int total = AntFrame.Overhead + length;
            if (available < total)
            {
                message = default;
                return false; // wait for the rest of this frame
            }

            byte expected = AntFrame.Checksum(_buffer.AsSpan(_start, total - 1));
            byte actual = _buffer[_start + total - 1];
            if (expected != actual)
            {
                // Malformed: drop the SYNC byte and rescan.
                _start++;
                DiscardedBytes++;
                continue;
            }

            var id = (AntMessageId)_buffer[_start + 2];
            var payload = _buffer.AsSpan(_start + 3, length).ToArray();
            _start += total;
            message = new AntMessage(id, payload);
            return true;
        }
    }

    private void Reset()
    {
        _start = 0;
        _end = 0;
    }

    private void EnsureCapacity(int incoming)
    {
        // Compact consumed prefix first.
        if (_start > 0)
        {
            int len = _end - _start;
            if (len > 0)
                Array.Copy(_buffer, _start, _buffer, 0, len);
            _start = 0;
            _end = len;
        }

        int required = _end + incoming;
        if (required <= _buffer.Length)
            return;

        int newSize = _buffer.Length * 2;
        while (newSize < required)
            newSize *= 2;
        Array.Resize(ref _buffer, newSize);
    }
}
