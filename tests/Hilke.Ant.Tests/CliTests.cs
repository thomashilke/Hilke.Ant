using Hilke.Ant.Cli;
using Hilke.Ant.Model;
using Hilke.Ant.Plus;
using Hilke.Ant.Plus.Common;
using Hilke.Ant.Plus.BicyclePower;
using Hilke.Ant.Plus.FitnessEquipment;
using Hilke.Ant.Protocol;
using Hilke.Ant.Testing;
using Xunit;

namespace Hilke.Ant.Tests;

public class CliTests
{
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
    private static async Task<(AntPlusNode node, SimulatedAntRadio sim, DeviceRegistry registry, AntSession session)> BuildAsync(Action<string>? log = null)
    {
        var transport = new InMemoryAntTransport();
        var sim = new SimulatedAntRadio(transport);
        var node = await AntPlusNode.OpenAsync(transport);
        var registry = new DeviceRegistry();
        var session = new AntSession(node, registry, log ?? (_ => { }));
        return (node, sim, registry, session);
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

        var wireId = new ChannelId(51234, 120, 1);
        var plusId = new AntPlusDeviceId(51234, 120, 1);
        var entry = registry.GetOrAdd(plusId, AntPlusDeviceCatalog.ProfileName(plusId.DeviceType));
        await session.ConnectAsync(entry.Token);

        byte ch = entry.ChannelNumber!.Value;
        byte[] hrm = { 0x04, 0xFF, 0xFF, 0xFF, 0x10, 0x27, 0x2A, 0x48 };

        bool ok = await WaitAsync(() =>
        {
            sim.InjectBroadcast(ch, wireId, hrm, rssi: -60);
            var e = registry.Snapshot().Single();
            return e.HeartRate == 72 && e.Connected;
        });
        Assert.True(ok);

        byte[] battery = { 0x52, 0xFF, 0x01, 0x10, 0x00, 0x00, 0x80, 0x35 };
        bool batteryOk = await WaitAsync(() =>
        {
            sim.InjectBroadcast(ch, wireId, battery, rssi: -60);
            var e = registry.Snapshot().Single();
            return e.Battery == BatteryStatus.Ok;
        });
        Assert.True(batteryOk);
        Assert.Equal(BatteryStatus.Ok, registry.Snapshot().Single().Battery);
    }

    // ----- Mutual exclusion -----

    [Fact]
    public async Task Scan_WhileConnected_IsRejected_ThenAllowedAfterDisconnect()
    {
        var (_, _, registry, session) = await BuildAsync();
        await using var _ = session;

        var plusId = new AntPlusDeviceId(51234, 120, 1);
        var entry = registry.GetOrAdd(plusId, AntPlusDeviceCatalog.ProfileName(plusId.DeviceType));
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

        var plusId = new AntPlusDeviceId(33333, 17, 5);
        var entry = registry.GetOrAdd(plusId, AntPlusDeviceCatalog.ProfileName(plusId.DeviceType));
        await session.ConnectAsync(entry.Token);
        byte ch = entry.ChannelNumber!.Value;

        var t = session.SetTargetPowerAsync(entry.Token, 250);
        await Task.Delay(50); // let the sim record the acknowledged page
        sim.InjectEvent(ch, ChannelResponseCode.EventTransferTxCompleted);

        // Confirm at the FE-C application level too (Command Status page 0x47), so the call
        // resolves immediately instead of waiting out the no-confirmation timeout.
        byte[] passStatus = { 0x47, FitnessEquipmentMonitor.TargetPowerPage, 0x01, (byte)FitnessEquipmentCommandStatus.Pass, 0xFF, 0xFF, 0xFF, 0xFF };
        var deviceId = new ChannelId(33333, 17, 5);
        await WaitAsync(() => { sim.InjectBroadcast(ch, deviceId, passStatus); return t.IsCompleted; });
        await t;

        Assert.Equal(FitnessEquipmentMonitor.BuildTargetPowerPage(250), sim.LastAcknowledgedPage);
    }

    [Fact]
    public async Task SetTargetPower_ReportsWhenTrainerRejectsCommand()
    {
        // Reproduces the reported bug: the CLI used to print "acknowledged" for every ANT-level
        // radio ack, even when the trainer's own FE-C application rejected the command (e.g. it
        // doesn't support ERG/target-power mode) and never actually changed the effective power.
        var log = new List<string>();
        var (_, sim, registry, session) = await BuildAsync(s => { lock (log) log.Add(s); });
        await using var _ = session;

        var plusId = new AntPlusDeviceId(33333, 17, 5);
        var entry = registry.GetOrAdd(plusId, AntPlusDeviceCatalog.ProfileName(plusId.DeviceType));
        await session.ConnectAsync(entry.Token);
        byte ch = entry.ChannelNumber!.Value;

        var t = session.SetTargetPowerAsync(entry.Token, 250);
        await Task.Delay(50);
        sim.InjectEvent(ch, ChannelResponseCode.EventTransferTxCompleted);

        byte[] notSupportedStatus = { 0x47, FitnessEquipmentMonitor.TargetPowerPage, 0x01, (byte)FitnessEquipmentCommandStatus.NotSupported, 0xFF, 0xFF, 0xFF, 0xFF };
        var deviceId = new ChannelId(33333, 17, 5);
        await WaitAsync(() => { sim.InjectBroadcast(ch, deviceId, notSupportedStatus); return t.IsCompleted; });
        await t;

        string line;
        lock (log) line = log.Single(l => l.Contains("target power"));
        Assert.Contains("not supported by trainer", line);
        Assert.DoesNotContain("acknowledged", line);
    }

    // ----- Bicycle Power calibration -----

    [Fact]
    public async Task Calibrate_ManualZero_SendsRequestAndResolvesResult()
    {
        var (_, sim, registry, session) = await BuildAsync();
        await using var _ = session;

        var wireId = new ChannelId(1234, 11, 1);
        var plusId = new AntPlusDeviceId(1234, 11, 1);
        var entry = registry.GetOrAdd(plusId, AntPlusDeviceCatalog.ProfileName(plusId.DeviceType));
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
            sim.InjectBroadcast(ch, wireId, response);
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

        var plusId = new AntPlusDeviceId(33333, 17, 5); // FE-C, not a power meter
        var entry = registry.GetOrAdd(plusId, AntPlusDeviceCatalog.ProfileName(plusId.DeviceType));
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

        var wireId = new ChannelId(1234, 11, 1);
        var plusId = new AntPlusDeviceId(1234, 11, 1);
        var entry = registry.GetOrAdd(plusId, AntPlusDeviceCatalog.ProfileName(plusId.DeviceType));
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
            sim.InjectBroadcast(ch, wireId, response);
            return t.IsCompleted;
        });
        await t;

        string line;
        lock (log) line = Assert.Single(log);
        Assert.Contains("succeeded", line);
        Assert.Contains("zero offset 300", line);
    }
}
