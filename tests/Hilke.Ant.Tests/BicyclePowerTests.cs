using Hilke.Ant.Plus;
using Xunit;

namespace Hilke.Ant.Tests;

public class BicyclePowerTests
{
    [Fact]
    public void PowerOnlyPage_DecodesInstantaneousFields()
    {
        var decoder = new BicyclePowerDecoder();
        // page 0x10, event 0, pedal 0xFF(invalid), cadence 90, accum 0, inst 200W (0x00C8)
        byte[] page = { 0x10, 0x00, 0xFF, 0x5A, 0x00, 0x00, 0xC8, 0x00 };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Equal(200, r.InstantaneousPower);
        Assert.Equal((byte)90, r.Cadence);
        Assert.Null(r.PedalPowerPercent);
        Assert.Equal((ushort)0, r.AccumulatedPower);
        Assert.Null(r.AveragePower); // no previous sample yet
    }

    [Fact]
    public void PowerOnlyPage_ComputesAveragePowerAcrossMessages()
    {
        var decoder = new BicyclePowerDecoder();
        // first sample
        decoder.TryDecode(new byte[] { 0x10, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0xC8, 0x00 }, out _);
        // second: event +2, accumulated +500 -> average 250 W over 2 events
        Assert.True(decoder.TryDecode(new byte[] { 0x10, 0x02, 0xFF, 0xFF, 0xF4, 0x01, 0xC8, 0x00 }, out var r));
        Assert.NotNull(r.AveragePower);
        Assert.Equal(250.0, r.AveragePower!.Value, 3);
    }

    [Fact]
    public void PowerOnlyPage_AveragePowerHandlesAccumulatorRollover()
    {
        var decoder = new BicyclePowerDecoder();
        decoder.TryDecode(new byte[] { 0x10, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0x64, 0x00 }, out _); // accum 0xFF00
        // event +1, accumulated wraps from 0xFF00 to 0x0064 => delta 0x0164 = 356
        Assert.True(decoder.TryDecode(new byte[] { 0x10, 0x01, 0xFF, 0xFF, 0x64, 0x00, 0x64, 0x00 }, out var r));
        Assert.Equal(356.0, r.AveragePower!.Value, 3);
    }

    [Fact]
    public void WrongPage_IsRejected()
    {
        var decoder = new BicyclePowerDecoder();
        Assert.False(decoder.TryDecode(new byte[] { 0x11, 0, 0, 0, 0, 0, 0, 0 }, out _));
    }
}
