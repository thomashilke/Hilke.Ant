using Hilke.Ant.Cli;
using Hilke.Ant.Model;
using Hilke.Ant.Plus;
using Hilke.Ant.Protocol;
using Hilke.Ant.Testing;
using Xunit;

namespace Hilke.Ant.Tests;

public class CliTests
{
    // ----- ProfileCatalog decode -----

    [Fact]
    public void ProfileCatalog_DecodesBatteryAndHeartRate()
    {
        var entry = new TrackedDeviceEntry
        {
            DeviceType = HeartRateMonitor.DeviceType,
            DecoderState = ProfileCatalog.CreateDecoderSet(HeartRateMonitor.DeviceType),
        };

        ProfileCatalog.Update(entry, new byte[] { 0x52, 0xFF, 0x01, 0x10, 0x00, 0x00, 0x80, 0x35 });
        Assert.Equal(BatteryStatus.Ok, entry.Battery);

        ProfileCatalog.Update(entry, new byte[] { 0x04, 0xFF, 0xFF, 0xFF, 0x10, 0x27, 0x2A, 0x48 });
        Assert.Equal(72, entry.HeartRate);
    }

    [Fact]
    public void ProfileCatalog_ComputesAveragePower_PerEntryStatefulDecoder()
    {
        var entry = new TrackedDeviceEntry
        {
            DeviceType = BicyclePowerMonitor.DeviceType,
            DecoderState = ProfileCatalog.CreateDecoderSet(BicyclePowerMonitor.DeviceType),
        };

        // event 0, accumulated 0
        ProfileCatalog.Update(entry, new byte[] { 0x10, 0x00, 0xFF, 90, 0x00, 0x00, 0xC8, 0x00 });
        // event 1, accumulated 200 -> average 200/1
        ProfileCatalog.Update(entry, new byte[] { 0x10, 0x01, 0xFF, 90, 0xC8, 0x00, 0xC8, 0x00 });

        Assert.Equal(200, entry.PowerWatts);
        Assert.Equal(200.0, entry.AveragePower!.Value, 3);
    }

    // ----- Completion -----

    [Fact]
    public void Completion_CompletesCommand()
    {
        Assert.Equal("connect ", Completion.Complete("con", Array.Empty<string>(), out _));
        Assert.Equal("scan ", Completion.Complete("sc", Array.Empty<string>(), out _));
    }

    [Fact]
    public void Completion_CompletesDeviceToken()
    {
        string result = Completion.Complete("connect 51", new[] { "51234" }, out var candidates);
        Assert.Equal("connect 51234 ", result);
        Assert.Empty(candidates);
    }

    [Fact]
    public void Completion_ScanArgCandidates()
    {
        string result = Completion.Complete("scan ", Array.Empty<string>(), out var candidates);
        Assert.Equal("scan o", result); // longest common prefix of on/off
        Assert.Equal(new[] { "on", "off" }, candidates);
    }

    // ----- integration helpers -----
    private static async Task<(AntDevice device, SimulatedAntRadio sim, DeviceRegistry registry, AntSession session)> BuildAsync()
    {
        var transport = new InMemoryAntTransport();
        var sim = new SimulatedAntRadio(transport);
        var device = new AntDevice(transport);
        await device.OpenAsync();
        await device.SetNetworkKeyAsync(1, new byte[8]);
        var registry = new DeviceRegistry();
        var session = new AntSession(device, registry, _ => { });
        return (device, sim, registry, session);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int attempts = 100, int delayMs = 20)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (condition())
                return true;
            await Task.Delay(delayMs);
        }
        return condition();
    }

    // ----- Connect + telemetry -----

    [Fact]
    public async Task ConnectThenInject_UpdatesTelemetry()
    {
        var (_, sim, registry, session) = await BuildAsync();
        await using var _ = session;

        var id = new ChannelId(51234, 120, 1);
        var entry = registry.GetOrAdd(id, ProfileCatalog.ProfileNameOrUnknown, ProfileCatalog.CreateDecoderSet);
        await session.ConnectAsync(entry.Token);

        byte ch = entry.ChannelNumber!.Value;
        byte[] hrm = { 0x04, 0xFF, 0xFF, 0xFF, 0x10, 0x27, 0x2A, 0x48 };

        bool ok = await WaitAsync(() =>
        {
            sim.InjectBroadcast(ch, id, hrm, rssi: -60);
            var e = registry.Snapshot().Single();
            return e.HeartRate == 72 && e.Connected;
        });

        Assert.True(ok);
    }

    // ----- Mutual exclusion -----

    [Fact]
    public async Task Scan_WhileConnected_IsRejected_ThenAllowedAfterDisconnect()
    {
        var (_, _, registry, session) = await BuildAsync();
        await using var _ = session;

        var id = new ChannelId(51234, 120, 1);
        var entry = registry.GetOrAdd(id, ProfileCatalog.ProfileNameOrUnknown, ProfileCatalog.CreateDecoderSet);
        await session.ConnectAsync(entry.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartScanAsync());

        await session.DisconnectAsync(entry.Token);
        await session.StartScanAsync(); // now permitted
        Assert.True(session.IsScanning);
        await session.StopScanAsync();
    }

    // ----- FE-C control -----

    [Fact]
    public async Task SetTargetPower_SendsAcknowledgedControlPage()
    {
        var (_, sim, registry, session) = await BuildAsync();
        await using var _ = session;

        var id = new ChannelId(33333, 17, 5);
        var entry = registry.GetOrAdd(id, ProfileCatalog.ProfileNameOrUnknown, ProfileCatalog.CreateDecoderSet);
        await session.ConnectAsync(entry.Token);
        byte ch = entry.ChannelNumber!.Value;

        var t = session.SetTargetPowerAsync(entry.Token, 250);
        await Task.Delay(50); // let the sim record the acknowledged page
        sim.InjectEvent(ch, ChannelResponseCode.EventTransferTxCompleted);
        await t;

        Assert.Equal(FitnessEquipmentMonitor.BuildTargetPowerPage(250), sim.LastAcknowledgedPage);
    }

    // ----- Bicycle Power calibration -----

    [Fact]
    public async Task Calibrate_ManualZero_SendsRequestAndResolvesResult()
    {
        var (_, sim, registry, session) = await BuildAsync();
        await using var _ = session;

        var id = new ChannelId(1234, 11, 1);
        var entry = registry.GetOrAdd(id, ProfileCatalog.ProfileNameOrUnknown, ProfileCatalog.CreateDecoderSet);
        await session.ConnectAsync(entry.Token);
        byte ch = entry.ChannelNumber!.Value;

        var t = session.RequestManualZeroAsync(entry.Token, TimeSpan.FromSeconds(2));
        await Task.Delay(50); // let the sim record the acknowledged page
        sim.InjectEvent(ch, ChannelResponseCode.EventTransferTxCompleted);

        byte[] response = { 0x01, 0xAC, 0x01, 0xFF, 0xFF, 0xFF, 0x2C, 0x01 };
        await WaitAsync(() =>
        {
            if (t.IsCompleted)
                return true;
            sim.InjectBroadcast(ch, id, response);
            return t.IsCompleted;
        });

        var r = await t;
        Assert.Equal(CalibrationOutcome.Success, r.Outcome);
        Assert.Equal((short)300, r.ZeroOffset);
        Assert.Equal(PowerMeterCalibrationSession.BuildManualZeroRequest(), sim.LastAcknowledgedPage);
    }

    [Fact]
    public async Task Calibrate_OnNonPowerDevice_Throws()
    {
        var (_, _, registry, session) = await BuildAsync();
        await using var _ = session;

        var id = new ChannelId(33333, 17, 5); // FE-C, not a power meter
        var entry = registry.GetOrAdd(id, ProfileCatalog.ProfileNameOrUnknown, ProfileCatalog.CreateDecoderSet);
        await session.ConnectAsync(entry.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RequestManualZeroAsync(entry.Token, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Calibrate_Command_DispatchesAndRendersResult()
    {
        var (_, sim, registry, session) = await BuildAsync();
        await using var _ = session;
        var log = new List<string>();
        var processor = new CommandProcessor(session, registry, s => { lock (log) log.Add(s); }, () => { });

        var id = new ChannelId(1234, 11, 1);
        var entry = registry.GetOrAdd(id, ProfileCatalog.ProfileNameOrUnknown, ProfileCatalog.CreateDecoderSet);
        await session.ConnectAsync(entry.Token);
        byte ch = entry.ChannelNumber!.Value;

        var t = processor.ExecuteAsync($"calibrate {entry.Token}");
        await Task.Delay(50);
        sim.InjectEvent(ch, ChannelResponseCode.EventTransferTxCompleted);
        byte[] response = { 0x01, 0xAC, 0x01, 0xFF, 0xFF, 0xFF, 0x2C, 0x01 };
        await WaitAsync(() =>
        {
            if (t.IsCompleted)
                return true;
            sim.InjectBroadcast(ch, id, response);
            return t.IsCompleted;
        });
        await t;

        string line;
        lock (log) line = Assert.Single(log);
        Assert.Contains("succeeded", line);
        Assert.Contains("zero offset 300", line);
    }
}
