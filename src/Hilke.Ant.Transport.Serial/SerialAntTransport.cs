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

    public SerialAntTransport(string portName, int baudRate = 115200)
    {
        _portName = portName ?? throw new ArgumentNullException(nameof(portName));
        _baudRate = baudRate;
    }

    public bool IsOpen => _port?.IsOpen ?? false;

    /// <summary>Enumerate available serial ports.</summary>
    public static string[] GetPortNames() => SerialPort.GetPortNames();

    public ValueTask OpenAsync(CancellationToken ct = default)
    {
        if (IsOpen)
            return ValueTask.CompletedTask;

        var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 1000,
            WriteTimeout = 1000,
        };
        port.Open();
        _port = port;
        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        var stream = RequireStream();
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

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

    public ValueTask DisposeAsync()
    {
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}
