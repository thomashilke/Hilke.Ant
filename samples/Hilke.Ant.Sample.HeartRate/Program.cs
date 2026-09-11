using Hilke.Ant;
using Hilke.Ant.Plus;
using Hilke.Ant.Transport.Serial;

// Manual hardware demo: connect an ANTUSB-m stick, export ANT_NETWORK_KEY as 16 hex chars
// (8 bytes) supplied under your ANT+ adopter agreement, then run with the COM/tty port name.
//
//   ANT_NETWORK_KEY=00112233...  dotnet run -- COM4
//
// Not part of the automated test suite (requires real hardware + a licensed key).

string port = args.Length > 0 ? args[0] : SerialAntTransport.GetPortNames().FirstOrDefault() ?? "COM3";

string? keyHex = Environment.GetEnvironmentVariable("ANT_NETWORK_KEY");
if (string.IsNullOrWhiteSpace(keyHex) || keyHex.Length != 16)
{
    Console.Error.WriteLine("Set ANT_NETWORK_KEY to your 8-byte (16 hex char) ANT+ network key.");
    return 1;
}

byte[] key = Convert.FromHexString(keyHex);

Console.WriteLine($"Opening ANT device on {port} ...");
await using var transport = new SerialAntTransport(port);
await using var device = new AntDevice(transport);
await device.OpenAsync();
Console.WriteLine($"Capabilities: {device.Capabilities.MaxChannels} channels, {device.Capabilities.MaxNetworks} networks.");

await device.SetNetworkKeyAsync(HeartRateMonitor.AntPlusNetwork, key);

var channel = await device.ConfigureChannelAsync(0, HeartRateMonitor.SlaveDefaults());
await using var hrm = new HeartRateMonitor(channel);
hrm.Channel.StateChanged += (_, e) => Console.WriteLine($"Channel: {e.OldState} -> {e.NewState}");

await channel.OpenAsync();
Console.WriteLine("Searching for a heart rate monitor. Press Ctrl+C to exit.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await foreach (var reading in hrm.ReadingsAsync(cts.Token))
        Console.WriteLine($"HR: {reading.ComputedHeartRate} bpm (page {reading.PageNumber}, beats {reading.BeatCount})");
}
catch (OperationCanceledException)
{
}

return 0;
