using Hilke.Ant.Plus;
using Hilke.Ant.Plus.HeartRate;
using Hilke.Ant.Transport.Serial;

// Manual hardware demo: connect an ANTUSB-m stick and run with the COM/tty port name.
//
//   dotnet run -- /dev/ttyUSB0

string port = args.Length > 0 ? args[0] : SerialAntTransport.GetPortNames().FirstOrDefault() ?? "COM3";

Console.WriteLine($"Opening ANT device on {port} ...");
await using var transport = new SerialAntTransport(port);
await using var node = await AntPlusNode.OpenAsync(transport);

var scan = await node.StartScanAsync();
Console.WriteLine("Searching for a heart rate monitor. Press Ctrl+C to exit.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

AntPlusDeviceId? found = null;
try
{
    await foreach (var sighting in scan.ReceiveAsync(cts.Token))
    {
        Console.WriteLine($"Seen: {sighting.ProfileName} {sighting.DeviceId.DeviceNumber} (rssi {sighting.Rssi?.ToString() ?? "?"})");
        if (sighting.DeviceId.DeviceType == HeartRateMonitor.DeviceType)
        {
            found = sighting.DeviceId;
            break;
        }
    }
}
catch (OperationCanceledException) { }

if (found is not { } id)
{
    Console.WriteLine("No heart rate monitor found.");
    return 0;
}
await scan.StopAsync();

await using var hrm = (HeartRateMonitor)await node.ConnectAsync(id);
hrm.StateChanged += (_, e) => Console.WriteLine($"Channel: {e.OldState} -> {e.NewState} ({e.Reason})");

try
{
    await foreach (var reading in hrm.ReadingsAsync(cts.Token))
        Console.WriteLine($"HR: {reading.ComputedHeartRate} bpm (page {reading.PageNumber}, beats {reading.BeatCount})");
}
catch (OperationCanceledException)
{
}

return 0;
