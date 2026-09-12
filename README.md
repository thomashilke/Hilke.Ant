# Hilke.Ant

A .NET 8 library for talking to ANT+ sensors (heart rate straps, bike power meters, smart
trainers) through an ANTUSB-m style USB stick. It's split into layers so consumers never touch
raw ANT protocol bytes:

| Project | Purpose |
|---|---|
| `src/Hilke.Ant` | Core ANT protocol: device/channel lifecycle, scan sessions, framing. Not ANT+-aware. |
| `src/Hilke.Ant.Plus` | The public surface: `AntPlusNode` (+ node-management types) in `Hilke.Ant.Plus`, each profile isolated in its own subnamespace — `Hilke.Ant.Plus.HeartRate`, `.BicyclePower` (+ calibration), `.FitnessEquipment` — and shared common-page decoding in `Hilke.Ant.Plus.Common`. |
| `src/Hilke.Ant.Transport.Serial` | `SerialAntTransport` — opens the USB dongle (`COMx` / `/dev/ttyUSBx`). |
| `src/Hilke.Ant.Testing` | `InMemoryAntTransport` + `SimulatedAntRadio`, a deterministic device double used by the tests and `--simulate` mode. |
| `tests/Hilke.Ant.Tests` | Unit + integration tests. |
| `samples/` | Runnable examples — see [Samples](#samples) below. |

A client only ever depends on `Hilke.Ant.Plus` (plus `Hilke.Ant.Transport.Serial` to open the
dongle): scanning returns discovered devices with decoded telemetry, and connecting returns one
`IAntPlusProfileConnection` per device that pumps its own messages and exposes typed events —
no raw channel, payload byte, or core exception ever leaks out.

## Getting started

Install the USB stick's CP210x VCP driver, then reference `Hilke.Ant.Plus` and
`Hilke.Ant.Transport.Serial`.

### Scan for devices

```csharp
using Hilke.Ant.Plus;
using Hilke.Ant.Transport.Serial;

await using var transport = new SerialAntTransport("/dev/ttyUSB0"); // or "COM3" on Windows
await using var node = await AntPlusNode.OpenAsync(transport);

var scan = await node.StartScanAsync();
await foreach (var sighting in scan.ReceiveAsync())
    Console.WriteLine($"{sighting.ProfileName} {sighting.DeviceId.DeviceNumber} rssi={sighting.Rssi}");
```

### Connect to a heart rate monitor and stream readings

```csharp
using Hilke.Ant.Plus.HeartRate;

var id = new AntPlusDeviceId(DeviceNumber: 12345, DeviceType: HeartRateMonitor.DeviceType, TransmissionType: 1);
await using var hrm = (HeartRateMonitor)await node.ConnectAsync(id);

hrm.StateChanged += (_, e) => Console.WriteLine($"{e.OldState} -> {e.NewState} ({e.Reason})");

await foreach (var reading in hrm.ReadingsAsync())
    Console.WriteLine($"{reading.ComputedHeartRate} bpm");
```

`BicyclePowerMonitor` and `FitnessEquipmentMonitor` follow the same `ConnectAsync` /
`ReadingsAsync` / `StateChanged` shape; see the samples for calibration and FE-C control.

## Limitations

- **Three profiles implemented**: heart rate, bicycle power (with manual-zero/auto-zero
  calibration), and FE-C (trainer control limited to target-power and basic-resistance pages —
  no track/wind resistance, capabilities, or user-configuration pages). Other ANT+ device
  profiles aren't decoded. `SetTargetPowerAsync`/`SetBasicResistanceAsync` completing only means
  the command bytes reached the trainer's radio — it does not mean the trainer accepted or
  applied them. Subscribe to `CommandStatusReceived` to see the trainer's own FE-C-level
  confirmation (page 0x47: pass/fail/not-supported/rejected); some trainers never send one.
- **One radio, one mode at a time**: the underlying dongle can either scan or have channels open,
  not both, and only one scan session runs at a time. `AntPlusNode.ConnectAsync` /
  `StartScanAsync` throw `AntPlusBusyException` if that invariant is violated — callers must stop
  scanning before connecting.
- **No auto-reconnect**: a dropped device (`AntPlusChannelState.Lost`, then `Searching`) is
  surfaced as a state event only (with a `Reason` distinguishing a radio-confirmed loss from the
  local inactivity-timeout heuristic, and an autonomous search-timeout closure from an explicit
  one); reconnecting after a real disconnect is the caller's responsibility.
- **Single transport per node**: one `AntPlusNode` talks to exactly one USB stick; there's no
  built-in multi-dongle coordination.
- **No persistence**: nothing is cached across process restarts — the CLI's device list, for
  example, is in-memory only.
- **Serial transport only**: `Hilke.Ant.Transport.Serial` wraps `System.IO.Ports.SerialPort`
  against a CP210x-style VCP driver; there's no BLE bridge or ANT-FS support.

## Samples

| Sample | What it shows |
|---|---|
| [`samples/Hilke.Ant.Sample.HeartRate`](samples/Hilke.Ant.Sample.HeartRate) | Minimal console app: scan, report every discovered device, connect to the first HRM found, stream heart rate over time. |
| [`samples/Hilke.Ant.Sample.PowerCalibration`](samples/Hilke.Ant.Sample.PowerCalibration) | Scan for a bike power meter, connect, stream power/cadence in the background, and run a manual-zero calibration request. |
| [`samples/Hilke.Ant.Cli`](samples/Hilke.Ant.Cli) | Full `Terminal.Gui` TUI: scan, connect/disconnect multiple devices, FE-C control, power calibration, device aliases. Run with `--simulate` for fabricated devices (no hardware needed) or `--port /dev/ttyUSB0` against a real stick. |

Run any of them with `dotnet run --project <path> -- <args>`, e.g.:

```sh
dotnet run --project samples/Hilke.Ant.Sample.HeartRate -- /dev/ttyUSB0
dotnet run --project samples/Hilke.Ant.Cli -- --simulate
```
