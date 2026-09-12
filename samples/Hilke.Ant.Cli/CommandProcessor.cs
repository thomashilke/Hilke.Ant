using System.Globalization;
using Hilke.Ant.Plus;
using Hilke.Ant.Plus.BicyclePower;

namespace Hilke.Ant.Cli;

/// <summary>
/// UI-agnostic command dispatcher. Parses a command line and drives the <see cref="AntSession"/> and
/// <see cref="DeviceRegistry"/>. Never throws out of <see cref="ExecuteAsync"/>: every error is logged.
/// </summary>
public sealed class CommandProcessor
{
    private readonly AntSession _session;
    private readonly DeviceRegistry _registry;
    private readonly Action<string> _log;
    private readonly Action _shutdown;

    public CommandProcessor(AntSession session, DeviceRegistry registry, Action<string> log, Action shutdown)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
    }

    public async Task ExecuteAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = args[0].ToLowerInvariant();
        try
        {
            switch (verb)
            {
                case "help": Help(); break;
                case "scan": await ScanAsync(args); break;
                case "devices": Devices(); break;
                case "connect": await ConnectAsync(args); break;
                case "disconnect": await DisconnectAsync(args); break;
                case "forget": await ForgetAsync(args); break;
                case "info": Info(args); break;
                case "alias": Alias(args); break;
                case "power": await PowerAsync(args); break;
                case "resistance": await ResistanceAsync(args); break;
                case "calibrate": await CalibrateAsync(args); break;
                case "quit":
                case "exit":
                    _shutdown();
                    break;
                default:
                    _log($"Unknown command '{verb}'. Type 'help' for the command list.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log($"{verb}: {ex.Message}");
        }
    }

    private void Help()
    {
        _log("Commands:");
        _log("  scan on|off              start/stop device discovery");
        _log("  devices                  list visible/connected devices");
        _log("  connect <token>          open a channel to a device");
        _log("  disconnect <token>       close a device's channel");
        _log("  forget <token>           disconnect and remove from the list");
        _log("  info <token>             show full device detail");
        _log("  alias <token> <name>     give a device a friendly name");
        _log("  power <token> <watts>    FE-C: set target power");
        _log("  resistance <token> <pct> FE-C: set basic resistance");
        _log("  calibrate <token> [auto on|off]  bike power: manual zero / auto-zero config");
        _log("  quit | exit              leave (Ctrl+Q)");
    }

    private async Task ScanAsync(string[] args)
    {
        if (args.Length < 2 || (args[1] != "on" && args[1] != "off"))
        {
            _log("usage: scan on|off");
            return;
        }
        try
        {
            if (args[1] == "on")
                await _session.StartScanAsync();
            else
                await _session.StopScanAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or AntPlusBusyException)
        {
            _log(ex.Message);
        }
    }

    private void Devices()
    {
        var snapshot = _registry.Snapshot();
        if (snapshot.Count == 0)
        {
            _log("No devices. Run 'scan on' to discover.");
            return;
        }
        foreach (var e in snapshot)
        {
            string name = e.Alias is { } a ? $"{e.Token} ({a})" : e.Token;
            string state = DeviceDisplay.FormatState(e);
            _log($"{name}  {e.ProfileName}  {state}  {DeviceDisplay.FormatPrimary(e)}");
        }
    }

    private async Task ConnectAsync(string[] args)
    {
        if (args.Length < 2)
        {
            _log("usage: connect <token>");
            return;
        }
        try
        {
            var entry = await _session.ConnectAsync(args[1]);
            _log($"connect: {entry.Token} ({entry.ProfileName}).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or AntPlusBusyException or AntPlusCommandException or AntPlusTimeoutException)
        {
            _log($"connect: {ex.Message}");
        }
    }

    private async Task DisconnectAsync(string[] args)
    {
        if (args.Length < 2)
        {
            _log("usage: disconnect <token>");
            return;
        }
        await _session.DisconnectAsync(args[1]);
    }

    private async Task ForgetAsync(string[] args)
    {
        if (args.Length < 2)
        {
            _log("usage: forget <token>");
            return;
        }
        if (_registry.TryResolve(args[1], out var entry))
        {
            if (entry.Connected)
                await _session.DisconnectAsync(entry.Token);
            _registry.Remove(entry.Token);
            _log($"forget: removed {entry.Token}.");
        }
        else
        {
            _log($"forget: unknown device '{args[1]}'.");
        }
    }

    private void Info(string[] args)
    {
        if (args.Length < 2)
        {
            _log("usage: info <token>");
            return;
        }
        if (!_registry.TryResolve(args[1], out var e))
        {
            _log($"info: unknown device '{args[1]}'.");
            return;
        }
        _log($"Device {e.Token}{(e.Alias is { } a ? $" ({a})" : "")}");
        _log($"  id: number={e.Id.DeviceNumber} type={e.Id.DeviceType} tx={e.Id.TransmissionType}");
        _log($"  profile: {e.ProfileName}   state: {DeviceDisplay.FormatState(e)}" +
             $"{(e.ChannelNumber is { } ch ? $"   channel: {ch}" : "")}");
        _log($"  telemetry: hr={Fmt(e.HeartRate)} power={Fmt(e.PowerWatts)}W avg={Fmt(e.AveragePower)}W " +
             $"cad={Fmt(e.Cadence)} speed={Fmt(e.SpeedMps)}m/s trainer={e.TrainerStatus ?? "--"}");
        _log($"  battery: {(e.Battery?.ToString() ?? "--")} {(e.BatteryVolts is { } v ? $"{v:F2}V" : "")}");
        if (e.Manufacturer is { } m)
            _log($"  manufacturer: id={m.ManufacturerId} model={m.ModelNumber} hw={m.HardwareRevision}");
        if (e.Product is { } p)
            _log($"  product: sw={p.SoftwareRevision} serial={p.SerialNumber}");
        _log($"  rssi: {(e.Rssi is { } r ? $"{r} dBm" : "--")}   last seen: {(DateTimeOffset.UtcNow - e.LastSeen).TotalSeconds:F0}s ago");
    }

    private void Alias(string[] args)
    {
        if (args.Length < 3)
        {
            _log("usage: alias <token> <name>");
            return;
        }
        if (!_registry.TryResolve(args[1], out var e))
        {
            _log($"alias: unknown device '{args[1]}'.");
            return;
        }
        _registry.SetAlias(e.Token, args[2]);
        _log($"alias: {e.Token} -> {args[2]}");
    }

    private async Task PowerAsync(string[] args)
    {
        if (args.Length < 3 || !ushort.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var watts))
        {
            _log("usage: power <token> <watts>");
            return;
        }
        try
        {
            await _session.SetTargetPowerAsync(args[1], watts);
        }
        catch (InvalidOperationException ex)
        {
            _log($"power: {ex.Message}");
        }
    }

    private async Task ResistanceAsync(string[] args)
    {
        if (args.Length < 3 || !double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            _log("usage: resistance <token> <percent>");
            return;
        }
        try
        {
            await _session.SetBasicResistanceAsync(args[1], percent);
        }
        catch (InvalidOperationException ex)
        {
            _log($"resistance: {ex.Message}");
        }
    }

    private async Task CalibrateAsync(string[] args)
    {
        if (args.Length < 2)
        {
            _log("usage: calibrate <token> [auto on|off]");
            return;
        }
        // Some power meters (e.g. Garmin Vector 2) take several seconds to complete a manual-zero
        // calibration before responding; a short timeout here reads as a false "timed out"/rejection.
        var timeout = TimeSpan.FromSeconds(15);
        try
        {
            PowerMeterCalibrationResult r;
            if (args.Length >= 3 && args[2].Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 4 || !TryParseOnOff(args[3], out bool enable))
                {
                    _log("usage: calibrate <token> auto on|off");
                    return;
                }
                r = await _session.ConfigureAutoZeroAsync(args[1], enable, timeout);
            }
            else
            {
                r = await _session.RequestManualZeroAsync(args[1], timeout);
            }
            _log(FormatCalibration(args[1], r));
        }
        catch (InvalidOperationException ex)
        {
            _log($"calibrate: {ex.Message}");
        }
    }

    private static bool TryParseOnOff(string value, out bool enabled)
    {
        switch (value.ToLowerInvariant())
        {
            case "on": case "true": case "1": enabled = true; return true;
            case "off": case "false": case "0": enabled = false; return true;
            default: enabled = false; return false;
        }
    }

    private static string FormatCalibration(string token, PowerMeterCalibrationResult r) => r.Outcome switch
    {
        CalibrationOutcome.Success => $"calibrate: {token} succeeded (zero offset {r.ZeroOffset}, auto-zero 0x{r.AutoZeroStatus:X2}).",
        CalibrationOutcome.Failed => $"calibrate: {token} rejected by sensor.",
        CalibrationOutcome.TimedOut => $"calibrate: {token} timed out waiting for the sensor.",
        CalibrationOutcome.TransmitFailed => $"calibrate: {token} transmit failed (no sensor acknowledgement).",
        _ => $"calibrate: {token} unknown result.",
    };

    private static string Fmt(int? v) => v?.ToString() ?? "--";
    private static string Fmt(double? v) => v is { } d ? d.ToString("F1") : "--";
}
