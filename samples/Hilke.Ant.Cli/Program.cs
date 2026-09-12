using Hilke.Ant.Protocol;
using Hilke.Ant.Cli;
using Hilke.Ant.Model;
using Hilke.Ant.Plus;
using Hilke.Ant.Plus.HeartRate;
using Hilke.Ant.Plus.BicyclePower;
using Hilke.Ant.Plus.FitnessEquipment;
using Hilke.Ant.Testing;
using Hilke.Ant.Transport.Serial;
using Microsoft.Extensions.Logging;
using Terminal.Gui;

string? port = null;
bool simulate = false;
int baud = 115200;
string? tracePath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length:
            port = args[++i];
            break;
        case "--simulate":
            simulate = true;
            break;
        case "--baud" when i + 1 < args.Length:
            _ = int.TryParse(args[++i], out baud);
            break;
        case "--trace" when i + 1 < args.Length:
            tracePath = args[++i];
            break;
        case "--help":
        case "-h":
            Console.WriteLine("usage: Hilke.Ant.Cli [--port COMx] [--baud 115200] [--simulate] [--trace path.log]");
            return 0;
    }
}

var registry = new DeviceRegistry();
AntPlusNode node;
SimulatedAntRadio? sim = null;
using var appCts = new CancellationTokenSource();
// Raw datalink trace: every TX/RX ANT frame (message id + hex bytes + UTC timestamp), logged by
// Hilke.Ant.AntDevice at LogLevel.Trace, independent of what this library currently decodes -
// a forensic record for diagnosing protocol behavior (see FileTraceLogger below).
using var traceLogger = tracePath is not null ? new FileTraceLogger(tracePath) : null;
if (tracePath is not null)
    TuiApp.AppendLog($"Datalink trace: {tracePath}");

if (simulate)
{
    var transport = new InMemoryAntTransport();
    sim = new SimulatedAntRadio(transport);
    node = await AntPlusNode.OpenAsync(transport, traceLogger);
}
else
{
    if (port is null)
    {
        var ports = SerialAntTransport.GetPortNames();
        if (ports.Length == 0)
        {
            Console.Error.WriteLine("No serial ports found. Pass --port COMx or use --simulate.");
            return 1;
        }
        port = ports[0];
    }

    var transport = new SerialAntTransport(port, baud);
    node = await AntPlusNode.OpenAsync(transport, traceLogger);
}

var session = new AntSession(node, registry, TuiApp.AppendLog);
var processor = new CommandProcessor(session, registry, TuiApp.AppendLog, () => Application.RequestStop());

if (sim is not null)
    _ = SimFeed.RunAsync(sim, session, registry, appCts.Token);

TuiApp.AppendLog(simulate
    ? "Simulation mode. Type 'scan on' to discover fabricated devices. 'help' for commands."
    : $"Connected to {port}. Type 'scan on' to discover devices. 'help' for commands.");

TuiApp.Run(session, registry, processor);

appCts.Cancel();
await session.DisposeAsync();
return 0;

/// <summary>
/// Background injector for <c>--simulate</c>: while scanning, emits three fabricated ANT+ devices on
/// channel 0; while connected, emits each connected device's pages on its channel and completes any
/// pending FE-C acknowledged transfer.
/// </summary>
internal static class SimFeed
{
    // ChannelId(number, type, tx)
    private static readonly ChannelId Hrm = new(51234, 120, 1);
    private static readonly ChannelId Power = new(12345, 11, 1);
    private static readonly ChannelId Fec = new(33333, 17, 5);

    public static async Task RunAsync(SimulatedAntRadio sim, AntSession session, DeviceRegistry registry, CancellationToken ct)
    {
        int tick = 0;
        byte powerEvent = 0;
        ushort powerAccum = 0;
        byte fecEvent = 0;
        ushort fecAccum = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                tick++;

                byte hr = (byte)(60 + tick % 40);
                ushort beatTime = (ushort)(tick * 800);
                ushort inst = (ushort)(180 + tick % 60);
                powerAccum = (ushort)(powerAccum + inst);
                fecAccum = (ushort)(fecAccum + inst);
                ushort speedMmS = 8000;

                byte[] hrmPage = { 0x04, 0xFF, 0xFF, 0xFF, (byte)(beatTime & 0xFF), (byte)(beatTime >> 8), (byte)(tick & 0xFF), hr };
                byte[] batteryPage = { 0x52, 0xFF, 0x01, 0x10, 0x00, 0x00, 0x80, 0x35 }; // Ok, 5.5V
                byte[] powerPage = { 0x10, powerEvent, 0xFF, 90, (byte)(powerAccum & 0xFF), (byte)(powerAccum >> 8), (byte)(inst & 0xFF), (byte)(inst >> 8) };
                byte[] fecGeneral = { 0x10, 0x19, (byte)(tick & 0xFF), (byte)(tick & 0xFF), (byte)(speedMmS & 0xFF), (byte)(speedMmS >> 8), 0xFF, 0x30 };
                byte[] fecTrainer = { 0x19, fecEvent, 85, (byte)(fecAccum & 0xFF), (byte)(fecAccum >> 8), (byte)(inst & 0xFF), (byte)(((inst >> 8) & 0x0F) | 0x30), 0x30 };

                if (session.IsScanning)
                {
                    sim.InjectBroadcast(0, Hrm, tick % 5 == 0 ? batteryPage : hrmPage, rssi: -60);
                    sim.InjectBroadcast(0, Power, powerPage, rssi: -55);
                    sim.InjectBroadcast(0, Fec, tick % 2 == 0 ? fecGeneral : fecTrainer, rssi: -70);
                }

                foreach (var e in registry.Snapshot())
                {
                    if (!e.Connected || e.ChannelNumber is not byte ch)
                        continue;

                    switch (e.DeviceType)
                    {
                        case HeartRateMonitor.DeviceType:
                            sim.InjectBroadcast(ch, Hrm, tick % 5 == 0 ? batteryPage : hrmPage, rssi: -60);
                            break;
                        case BicyclePowerMonitor.DeviceType:
                            sim.InjectBroadcast(ch, Power, powerPage, rssi: -55);
                            break;
                        case FitnessEquipmentMonitor.DeviceType:
                            sim.InjectBroadcast(ch, Fec, tick % 2 == 0 ? fecGeneral : fecTrainer, rssi: -70);
                            // Complete any pending acknowledged control transfer, then confirm it at
                            // the FE-C application level via a Command Status page (0x47), echoing
                            // back the command just sent so `power`/`resistance` report a real status
                            // instead of only the radio-level ack.
                            sim.InjectEvent(ch, Hilke.Ant.Protocol.ChannelResponseCode.EventTransferTxCompleted);
                            if (sim.LastAcknowledgedPage is { Length: > 0 } lastCommand)
                                sim.InjectBroadcast(ch, Fec, new byte[] { 0x47, lastCommand[0], 0xFF, 0x00, 0xFF, 0xFF, 0xFF, 0xFF }, rssi: -70);
                            break;
                    }
                }

                powerEvent++;
                fecEvent++;
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}

/// <summary>
/// Minimal file-backed <see cref="ILogger"/>: appends every log message as one line, verbatim.
/// Enabled via <c>--trace path.log</c>; captures the raw ANT datalink trace that
/// <see cref="Hilke.Ant.AntDevice"/> emits at <see cref="LogLevel.Trace"/> for every TX/RX frame
/// (message id, hex bytes, UTC timestamp) - independent of what this library currently decodes,
/// for forensic replay/analysis after the fact.
/// </summary>
internal sealed class FileTraceLogger : ILogger, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileTraceLogger(string path) =>
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
            _writer.WriteLine(formatter(state, exception));
    }

    public void Dispose() => _writer.Dispose();
}
