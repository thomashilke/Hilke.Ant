using Hilke.Ant;
using Hilke.Ant.Model;
using Hilke.Ant.Protocol;
using Hilke.Ant.Testing;
using Xunit;

namespace Hilke.Ant.Tests;

public class DeviceTests
{
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }
        return condition();
    }

    private static async Task<(AntDevice device, SimulatedAntRadio sim, InMemoryAntTransport transport)> OpenAsync()
    {
        var transport = new InMemoryAntTransport();
        var sim = new SimulatedAntRadio(transport);
        var device = new AntDevice(transport) { CommandTimeout = TimeSpan.FromMilliseconds(300) };
        await device.OpenAsync();
        return (device, sim, transport);
    }

    private static ChannelConfiguration SlaveConfig(TimeSpan? inactivity = null) => new()
    {
        Type = ChannelType.BidirectionalSlave,
        ChannelId = ChannelId.Wildcard(),
        RfFrequency = AntConstants.AntPlusRfFrequency,
        ChannelPeriod = 8070,
        UseExtendedMessages = true,
        InactivityTimeout = inactivity,
    };

    [Fact]
    public async Task Open_PopulatesCapabilities()
    {
        var (device, sim, _) = await OpenAsync();
        await using (device)
        await using (sim)
        {
            Assert.Equal(8, device.Capabilities.MaxChannels);
            Assert.Equal(3, device.Capabilities.MaxNetworks);
        }
    }

    [Fact]
    public async Task ChannelLifecycle_ExactStateSequence()
    {
        var (device, sim, _) = await OpenAsync();
        await using (device)
        await using (sim)
        {
            var channel = await device.ConfigureChannelAsync(0, SlaveConfig(TimeSpan.FromMilliseconds(150)));
            Assert.Equal(ChannelState.Configured, channel.State);

            var transitions = new List<(ChannelState, ChannelState)>();
            channel.StateChanged += (_, e) =>
            {
                lock (transitions) transitions.Add((e.OldState, e.NewState));
            };

            var deviceId = new ChannelId(0x1234, 0x78, 0x01);
            var received = new List<AntDataMessage>();
            var pump = Task.Run(async () =>
            {
                await foreach (var m in channel.ReceiveAsync())
                {
                    lock (received) received.Add(m);
                }
            });

            await channel.OpenAsync();
            Assert.Equal(ChannelState.Searching, channel.State);

            sim.InjectBroadcast(0, deviceId, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Active), "did not become Active");
            Assert.Equal(deviceId, channel.TrackedDevice);
            Assert.True(await WaitForAsync(() => { lock (received) return received.Count >= 1; }));

            // No RX past inactivity timeout -> Inactive.
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Inactive), "did not become Inactive");

            // Next broadcast -> Active again.
            sim.InjectBroadcast(0, deviceId, new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 });
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Active), "did not re-activate");

            await channel.CloseAsync();
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Configured), "did not close to Configured");

            await channel.UnassignAsync();
            Assert.Equal(ChannelState.Unconfigured, channel.State);

            await pump;

            (ChannelState, ChannelState)[] expected =
            {
                (ChannelState.Configured, ChannelState.Searching),
                (ChannelState.Searching, ChannelState.Active),
                (ChannelState.Active, ChannelState.Inactive),
                (ChannelState.Inactive, ChannelState.Active),
                (ChannelState.Active, ChannelState.Closing),
                (ChannelState.Closing, ChannelState.Configured),
                (ChannelState.Configured, ChannelState.Unconfigured),
            };
            lock (transitions)
                Assert.Equal(expected, transitions.ToArray());
        }
    }

    [Fact]
    public async Task MutualExclusion_ChannelOpenBlocksScan()
    {
        var (device, sim, _) = await OpenAsync();
        await using (device)
        await using (sim)
        {
            var channel = await device.ConfigureChannelAsync(1, SlaveConfig());
            await channel.OpenAsync();

            await Assert.ThrowsAsync<RadioBusyException>(
                () => device.StartScanAsync(new ScanConfiguration()));
        }
    }

    [Fact]
    public async Task MutualExclusion_ScanBlocksChannelOpenAndConfigure()
    {
        var (device, sim, _) = await OpenAsync();
        await using (device)
        await using (sim)
        {
            // Configure (but do not open) a channel on ch1, then scan.
            var channel = await device.ConfigureChannelAsync(1, SlaveConfig());
            var scan = await device.StartScanAsync(new ScanConfiguration());

            await Assert.ThrowsAsync<RadioBusyException>(() => channel.OpenAsync());
            await Assert.ThrowsAsync<RadioBusyException>(
                () => device.ConfigureChannelAsync(2, SlaveConfig()));

            await scan.StopAsync();

            // After stopping the scan, opening a channel succeeds.
            var ch2 = await device.ConfigureChannelAsync(2, SlaveConfig());
            await ch2.OpenAsync();
            Assert.Equal(ChannelState.Searching, ch2.State);
        }
    }

    [Fact]
    public async Task Scan_YieldsDistinctDevices()
    {
        var (device, sim, _) = await OpenAsync();
        await using (device)
        await using (sim)
        {
            var scan = await device.StartScanAsync(new ScanConfiguration());

            var ids = new[]
            {
                new ChannelId(0x0001, 0x78, 0x01),
                new ChannelId(0x0002, 0x78, 0x01),
                new ChannelId(0x0003, 0x78, 0x01),
            };

            var seen = new List<ChannelId>();
            var pump = Task.Run(async () =>
            {
                await foreach (var m in scan.ReceiveAsync())
                {
                    lock (seen) seen.Add(m.Device);
                    if (seen.Count >= 3) break;
                }
            });

            foreach (var id in ids)
                sim.InjectBroadcast(0, id, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });

            await pump;
            lock (seen)
            {
                Assert.Equal(3, seen.Count);
                Assert.Equal(ids.OrderBy(i => i.DeviceNumber), seen.OrderBy(i => i.DeviceNumber));
            }

            await scan.StopAsync();
        }
    }

    [Fact]
    public async Task CommandPipeline_WrongState_ThrowsCommandException()
    {
        var (device, sim, _) = await OpenAsync();
        await using (device)
        await using (sim)
        {
            sim.FailNextCommand(AntMessageId.AssignChannel, ChannelResponseCode.ChannelInWrongState);
            var ex = await Assert.ThrowsAsync<AntCommandException>(
                () => device.ConfigureChannelAsync(0, SlaveConfig()));
            Assert.Equal(ChannelResponseCode.ChannelInWrongState, ex.Code);
            Assert.Equal(AntMessageId.AssignChannel, ex.Command);
        }
    }

    [Fact]
    public async Task CommandPipeline_NoReply_ThrowsTimeout()
    {
        var (device, sim, _) = await OpenAsync();
        await using (device)
        await using (sim)
        {
            sim.SwallowNextCommand(AntMessageId.AssignChannel);
            await Assert.ThrowsAsync<AntTimeoutException>(
                () => device.ConfigureChannelAsync(0, SlaveConfig()));
        }
    }

    [Fact]
    public async Task MasterTx_Acknowledged_CompletesAndFaults()
    {
        var (device, sim, _) = await OpenAsync();
        await using (device)
        await using (sim)
        {
            var cfg = SlaveConfig() with { Type = ChannelType.BidirectionalMaster };
            var channel = await device.ConfigureChannelAsync(0, cfg);
            await channel.OpenAsync();

            var page = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            // Success path.
            var sendOk = channel.SendAcknowledgedAsync(page);
            sim.InjectEvent(0, ChannelResponseCode.EventTransferTxCompleted);
            await sendOk; // completes without throwing

            // Failure path.
            var sendFail = channel.SendAcknowledgedAsync(page);
            sim.InjectEvent(0, ChannelResponseCode.EventTransferTxFailed);
            await Assert.ThrowsAsync<AntCommandException>(() => sendFail);
        }
    }
}
