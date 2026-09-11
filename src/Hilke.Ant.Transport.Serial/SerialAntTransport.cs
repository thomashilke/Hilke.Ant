using System.IO.Ports;
using Hilke.Ant.Transport;

namespace Hilke.Ant.Transport.Serial;

/// <summary>
/// Cross-platform serial transport for ANTUSB-m style sticks (CP210x VCP driver: COMx on
/// Windows, /dev/ttyUSBx on Linux). Framing lives in the core; this only moves raw bytes.
/// </summary>
public sealed class SerialAntTransport : IAntTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private SerialPort? _port;

    /// <summary>Create a transport for the given serial port name, not yet opened.</summary>
    public SerialAntTransport(string portName, int baudRate = 115200)
    {
        _portName = portName ?? throw new ArgumentNullException(nameof(portName));
        _baudRate = baudRate;
    }

    /// <summary>Whether the serial port currently has an open connection to the device.</summary>
    public bool IsOpen => _port?.IsOpen ?? false;

    /// <summary>Enumerate available serial ports.</summary>
    public static string[] GetPortNames() => SerialPort.GetPortNames();

    /// <summary>Open the serial port.</summary>
    public ValueTask OpenAsync(CancellationToken ct = default)
    {
        if (IsOpen)
            return ValueTask.CompletedTask;

        _port = Open(assertHandshakeLines: true) ?? Open(assertHandshakeLines: false)!;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Opens the port, asserting DTR/RTS first (some ANTUSB-m sticks need them raised).
    /// Several Linux USB-serial drivers — e.g. the generic/quirk driver certain ANTUSB-m sticks
    /// bind to instead of cp210x — don't implement the DTR/RTS modem-control ioctls and fail
    /// <see cref="SerialPort.Open"/> outright ("Inappropriate ioctl for device"). DTR/RTS aren't
    /// required for ANT radio operation, so on that failure we retry without them.
    /// </summary>
    private SerialPort? Open(bool assertHandshakeLines)
    {
        var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000,
        };
        if (assertHandshakeLines)
        {
            port.DtrEnable = true;
            port.RtsEnable = true;
        }
        try
        {
            port.Open();
            return port;
        }
        catch (IOException) when (assertHandshakeLines)
        {
            port.Dispose();
            return null;
        }
    }

    /// <summary>Write one complete, already-framed ANT message to the device.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        var stream = RequireStream();
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Read bytes from the device into <paramref name="buffer"/>. Returns 0 when the port is closed.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var port = _port;
        if (port is null || !port.IsOpen)
            return 0;
        try
        {
            return await port.BaseStream.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (IOException)
        {
            // treat as closed
            return 0;
        }
    }

    /// <summary>Close the serial port.</summary>
    public ValueTask CloseAsync(CancellationToken ct = default)
    {
        if (_port is { IsOpen: true } port)
            port.Close();
        return ValueTask.CompletedTask;
    }

    private Stream RequireStream()
    {
        var port = _port;
        if (port is null || !port.IsOpen)
            throw new InvalidOperationException("Serial transport is not open.");
        return port.BaseStream;
    }

    /// <summary>Close and dispose the serial port.</summary>
    public ValueTask DisposeAsync()
    {
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}
