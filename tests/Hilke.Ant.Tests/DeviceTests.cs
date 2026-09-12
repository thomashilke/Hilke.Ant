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
        RfFrequency = 57, // arbitrary valid test frequency, unrelated to ANT+
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

            var transitions = new List<(ChannelState, ChannelState, ChannelTransitionReason)>();
            channel.StateChanged += (_, e) =>
            {
                lock (transitions) transitions.Add((e.OldState, e.NewState, e.Reason));
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
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Tracking), "did not become Tracking");
            Assert.Equal(deviceId, channel.TrackedDevice);
            Assert.True(await WaitForAsync(() => { lock (received) return received.Count >= 1; }));

            // No RX past inactivity timeout -> Lost (local heuristic, not a radio-confirmed re-search).
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Lost), "did not become Lost");

            // Next broadcast -> Tracking again.
            sim.InjectBroadcast(0, deviceId, new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 });
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Tracking), "did not re-track");

            await channel.CloseAsync();
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Configured), "did not close to Configured");

            await channel.UnassignAsync();
            Assert.Equal(ChannelState.Unconfigured, channel.State);

            await pump;

            (ChannelState, ChannelState, ChannelTransitionReason)[] expected =
            {
                (ChannelState.Configured, ChannelState.Searching, ChannelTransitionReason.Requested),
                (ChannelState.Searching, ChannelState.Tracking, ChannelTransitionReason.DataReceived),
                (ChannelState.Tracking, ChannelState.Lost, ChannelTransitionReason.InactivityTimeout),
                (ChannelState.Lost, ChannelState.Tracking, ChannelTransitionReason.DataReceived),
                (ChannelState.Tracking, ChannelState.Closing, ChannelTransitionReason.Requested),
                (ChannelState.Closing, ChannelState.Configured, ChannelTransitionReason.Requested),
                (ChannelState.Configured, ChannelState.Unconfigured, ChannelTransitionReason.Requested),
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
    public async Task DeviceLoss_RxFailGoToSearch_ThenSearchTimeout_RevertsToConfigured()
    {
        var (device, sim, _) = await OpenAsync();
        await using (device)
        await using (sim)
        {
            var channel = await device.ConfigureChannelAsync(1, SlaveConfig());

            // Regression: a consumer reacting to the Configured transition (e.g. "channel closed,
            // safe to scan again") must never observe a stale "channel is open" rejection. Capturing
            // the scan attempt synchronously from inside the StateChanged handler pins it to the
            // exact instant the transition is published - deterministic, not timing-dependent.
            Task<ScanSession>? scanOnClose = null;
            ChannelTransitionReason? lostReason = null;
            ChannelTransitionReason? closedReason = null;
            channel.StateChanged += (_, e) =>
            {
                if (e.NewState == ChannelState.Lost)
                    lostReason = e.Reason;
                if (e.NewState == ChannelState.Configured)
                {
                    closedReason = e.Reason;
                    scanOnClose = device.StartScanAsync(new ScanConfiguration());
                }
            };

            await channel.OpenAsync();

            var deviceId = new ChannelId(0x1234, 0x78, 0x01);
            sim.InjectBroadcast(1, deviceId, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Tracking), "did not become Tracking");

            // Device goes out of range: too many consecutive missed messages drops the radio into
            // its own low-priority re-search, reported as EventRxFailGoToSearch.
            sim.InjectEvent(1, ChannelResponseCode.EventRxFailGoToSearch);
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Lost), "did not become Lost on EventRxFailGoToSearch");
            // Distinguishes a radio-confirmed loss from the local InactivityTimeout heuristic -
            // exactly the ambiguity a consumer needs resolved to know whether a re-search is
            // actually in flight at the radio level.
            Assert.Equal(ChannelTransitionReason.DeviceLost, lostReason);

            // The channel is still open (searching for the lost device), so the scan/channel
            // mutual exclusion still blocks starting a scan.
            await Assert.ThrowsAsync<RadioBusyException>(() => device.StartScanAsync(new ScanConfiguration()));

            // The device never comes back: the re-search itself times out, and the radio
            // autonomously closes the channel, reported as EventRxSearchTimeout.
            sim.InjectEvent(1, ChannelResponseCode.EventRxSearchTimeout);
            Assert.True(await WaitForAsync(() => channel.State == ChannelState.Configured), "did not revert to Configured on EventRxSearchTimeout");
            // Distinguishes an autonomous search-timeout closure from an explicit CloseAsync -
            // the other ambiguity a consumer needs resolved.
            Assert.Equal(ChannelTransitionReason.SearchTimedOut, closedReason);

            // The scan started synchronously from the StateChanged handler above must have seen
            // the open-channel bookkeeping already released, not a stale busy rejection.
            Assert.NotNull(scanOnClose);
            var scan = await scanOnClose!;
            await scan.StopAsync();
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
