using Hilke.Ant;
using Hilke.Ant.Model;
using Hilke.Ant.Plus;
using Hilke.Ant.Protocol;
using Hilke.Ant.Testing;
using Xunit;

namespace Hilke.Ant.Tests;

public class PowerMeterCalibrationTests
{
    // ----- decoder -----

    [Fact]
    public void Decoder_Success_ParsesStatusAndOffset()
    {
        var decoder = new CalibrationDecoder();
        byte[] page = { 0x01, 0xAC, 0x01, 0xFF, 0xFF, 0xFF, 0x2C, 0x01 };
        Assert.True(decoder.TryDecode(page, out var resp));
        Assert.True(resp.Success);
        Assert.Equal((byte)0x01, resp.AutoZeroStatus);
        Assert.Equal((short)300, resp.CalibrationData);
    }

    [Fact]
    public void Decoder_SignedOffset_IsNegative()
    {
        var decoder = new CalibrationDecoder();
        byte[] page = { 0x01, 0xAC, 0x00, 0xFF, 0xFF, 0xFF, 0x9C, 0xFF };
        Assert.True(decoder.TryDecode(page, out var resp));
        Assert.Equal((short)-100, resp.CalibrationData);
    }

    [Fact]
    public void Decoder_Fail_And_RejectsNonResponses()
    {
        var decoder = new CalibrationDecoder();

        byte[] fail = { 0x01, 0xAF, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00 };
        Assert.True(decoder.TryDecode(fail, out var failResp));
        Assert.False(failResp.Success);

        byte[] request = { 0x01, 0xAA, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.False(decoder.TryDecode(request, out _));

        byte[] powerPage = { 0x10, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00 };
        Assert.False(decoder.TryDecode(powerPage, out _));
    }

    [Fact]
    public void RequestBuilders_MatchWireFormat()
    {
        Assert.Equal(
            new byte[] { 0x01, 0xAA, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
            PowerMeterCalibrationSession.BuildManualZeroRequest());
        Assert.Equal(
            new byte[] { 0x01, 0xAB, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
            PowerMeterCalibrationSession.BuildAutoZeroConfigRequest(true));
        Assert.Equal(
            new byte[] { 0x01, 0xAB, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
            PowerMeterCalibrationSession.BuildAutoZeroConfigRequest(false));
    }

    // ----- integration helpers -----

    private static async Task<(AntDevice device, SimulatedAntRadio sim, AntChannel channel, BicyclePowerMonitor monitor)> BuildMonitorAsync()
    {
        var transport = new InMemoryAntTransport();
        var sim = new SimulatedAntRadio(transport);
        var device = new AntDevice(transport);
        await device.OpenAsync();
        await device.SetNetworkKeyAsync(1, new byte[8]);

        var id = new ChannelId(1234, 11, 1);
        var channel = await device.ConfigureChannelAsync(1, BicyclePowerMonitor.SlaveDefaults(id));
        var monitor = new BicyclePowerMonitor(channel);
        await channel.OpenAsync();
        return (device, sim, channel, monitor);
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

    // ----- integration -----

    [Fact]
    public async Task RequestManualZero_Success_ResolvesWithOffset()
    {
        var (_, sim, channel, monitor) = await BuildMonitorAsync();
        await using var _ = monitor;
        var id = new ChannelId(1234, 11, 1);

        var t = monitor.RequestManualZeroAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        sim.InjectEvent(channel.ChannelNumber, ChannelResponseCode.EventTransferTxCompleted);

        byte[] response = { 0x01, 0xAC, 0x01, 0xFF, 0xFF, 0xFF, 0x2C, 0x01 };
        await WaitAsync(() =>
        {
            if (t.IsCompleted)
                return true;
            sim.InjectBroadcast(channel.ChannelNumber, id, response);
            return t.IsCompleted;
        });

        var r = await t;
        Assert.Equal(CalibrationOutcome.Success, r.Outcome);
        Assert.Equal((short)300, r.ZeroOffset);
        Assert.Equal(PowerMeterCalibrationSession.BuildManualZeroRequest(), sim.LastAcknowledgedPage);
    }

    [Fact]
    public async Task RequestManualZero_Fail_ResolvesFailed()
    {
        var (_, sim, channel, monitor) = await BuildMonitorAsync();
        await using var _ = monitor;
        var id = new ChannelId(1234, 11, 1);

        var t = monitor.RequestManualZeroAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        sim.InjectEvent(channel.ChannelNumber, ChannelResponseCode.EventTransferTxCompleted);

        byte[] response = { 0x01, 0xAF, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00 };
        await WaitAsync(() =>
        {
            if (t.IsCompleted)
                return true;
            sim.InjectBroadcast(channel.ChannelNumber, id, response);
            return t.IsCompleted;
        });

        var r = await t;
        Assert.Equal(CalibrationOutcome.Failed, r.Outcome);
    }

    [Fact]
    public async Task RequestManualZero_NoResponse_TimesOut()
    {
        var (_, sim, channel, monitor) = await BuildMonitorAsync();
        await using var _ = monitor;

        var t = monitor.RequestManualZeroAsync(TimeSpan.FromMilliseconds(300));
        await Task.Delay(50);
        sim.InjectEvent(channel.ChannelNumber, ChannelResponseCode.EventTransferTxCompleted);

        var r = await t;
        Assert.Equal(CalibrationOutcome.TimedOut, r.Outcome);
    }

    [Fact]
    public async Task SecondRequest_WhileAwaiting_Throws()
    {
        var (_, sim, channel, monitor) = await BuildMonitorAsync();
        await using var _ = monitor;

        var t = monitor.RequestManualZeroAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        sim.InjectEvent(channel.ChannelNumber, ChannelResponseCode.EventTransferTxCompleted);

        await WaitAsync(() => monitor.Calibration.State == PowerMeterCalibrationState.AwaitingResponse);
        Assert.Equal(PowerMeterCalibrationState.AwaitingResponse, monitor.Calibration.State);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => monitor.RequestManualZeroAsync(TimeSpan.FromSeconds(2)));

        // let the first request drain to avoid an unobserved timeout task
        byte[] response = { 0x01, 0xAC, 0x01, 0xFF, 0xFF, 0xFF, 0x2C, 0x01 };
        await WaitAsync(() =>
        {
            if (t.IsCompleted)
                return true;
            sim.InjectBroadcast(channel.ChannelNumber, new ChannelId(1234, 11, 1), response);
            return t.IsCompleted;
        });
        await t;
    }
}
