using Hilke.Ant.Plus;
using Hilke.Ant.Transport.Serial;

// Manual hardware demo: connect an ANTUSB-m stick and a bike power meter, run with the COM/tty port name.
//
//   dotnet run -- /dev/ttyUSB0

string port = args.Length > 0 ? args[0] : SerialAntTransport.GetPortNames().FirstOrDefault() ?? "COM3";

Console.WriteLine($"Opening ANT device on {port} ...");
await using var transport = new SerialAntTransport(port);
await using var node = await AntPlusNode.OpenAsync(transport);

var scan = await node.StartScanAsync();
Console.WriteLine("Searching for a bicycle power meter. Press Ctrl+C to exit.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

AntPlusDeviceId? found = null;
try
{
    await foreach (var sighting in scan.ReceiveAsync(cts.Token))
    {
        Console.WriteLine($"Seen: {sighting.ProfileName} {sighting.DeviceId.DeviceNumber} (rssi {sighting.Rssi?.ToString() ?? "?"})");
        if (sighting.DeviceId.DeviceType == BicyclePowerMonitor.DeviceType)
        {
            found = sighting.DeviceId;
            break;
        }
    }
}
catch (OperationCanceledException) { }

if (found is not { } id)
{
    Console.WriteLine("No power meter found.");
    return 0;
}
await scan.StopAsync();

await using var power = (BicyclePowerMonitor)await node.ConnectAsync(id);
power.StateChanged += (_, e) => Console.WriteLine($"Channel: {e.OldState} -> {e.NewState}");
_ = ConsumeReadingsAsync(power, cts.Token);

Console.WriteLine("Requesting manual-zero calibration ...");
var result = await power.RequestManualZeroAsync(TimeSpan.FromSeconds(5), cts.Token);
Console.WriteLine($"Calibration: {result.Outcome} (zero offset {result.ZeroOffset}, auto-zero 0x{result.AutoZeroStatus:X2})");

return 0;

static async Task ConsumeReadingsAsync(BicyclePowerMonitor power, CancellationToken ct)
{
    try
    {
        await foreach (var reading in power.ReadingsAsync(ct))
            Console.WriteLine($"Power: {reading.InstantaneousPower} W  cadence: {reading.Cadence?.ToString() ?? "--"} rpm");
    }
    catch (OperationCanceledException) { }
}
