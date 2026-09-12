using Hilke.Ant.Plus.HeartRate;
using Xunit;

namespace Hilke.Ant.Tests;

public class HeartRateTests
{
    [Fact]
    public void HrmDecode_ExtractsFields()
    {
        var decoder = new HeartRatePageDecoder();
        byte[] page = { 0x04, 0xFF, 0xFF, 0xFF, 0x10, 0x27, 0x2A, 0x48 };

        Assert.True(decoder.TryDecode(page, out var reading));
        Assert.Equal(0x48, reading.ComputedHeartRate); // 72 bpm
        Assert.Equal(0x2A, reading.BeatCount);
        Assert.Equal(0x2710, reading.BeatEventTime);
        Assert.Equal(0x04, reading.PageNumber);
        Assert.Equal((int)(unchecked((ushort)(10000 - 65535)) * 1000L / 1024L), reading.RrIntervalMs);
    }

    [Fact]
    public void Page4_ComputesSelfContainedRrInterval()
    {
        var decoder = new HeartRatePageDecoder();
        // previous=1000 (0xE8,0x03), current=1750 (0xD6,0x06)
        byte[] page = { 0x04, 0xFF, 0xE8, 0x03, 0xD6, 0x06, 0x2A, 0x48 };

        Assert.True(decoder.TryDecode(page, out var reading));
        Assert.Equal(732, reading.RrIntervalMs);
    }

    [Fact]
    public void NonPage4_FallsBackToCrossMessageRr_WhenBeatCountAdvancesByOne()
    {
        var decoder = new HeartRatePageDecoder();
        byte[] first = { 0x00, 0xFF, 0xFF, 0xFF, 0xE8, 0x03, 5, 0x48 }; // beatEventTime=1000, beatCount=5
        byte[] second = { 0x00, 0xFF, 0xFF, 0xFF, 0x08, 0x07, 6, 0x48 }; // beatEventTime=1800, beatCount=6

        Assert.True(decoder.TryDecode(first, out var firstReading));
        Assert.Null(firstReading.RrIntervalMs);
        Assert.True(decoder.TryDecode(second, out var secondReading));
        Assert.Equal(781, secondReading.RrIntervalMs);
    }

    [Fact]
    public void NonPage4_SuppressesRr_WhenBeatCountSkips()
    {
        var decoder = new HeartRatePageDecoder();
        byte[] first = { 0x00, 0xFF, 0xFF, 0xFF, 0xE8, 0x03, 5, 0x48 }; // beatEventTime=1000, beatCount=5
        byte[] second = { 0x00, 0xFF, 0xFF, 0xFF, 0x08, 0x07, 8, 0x48 }; // beatEventTime=1800, beatCount=8 (skipped 2 beats)

        Assert.True(decoder.TryDecode(first, out var firstReading));
        Assert.Null(firstReading.RrIntervalMs);
        Assert.True(decoder.TryDecode(second, out var secondReading));
        Assert.Null(secondReading.RrIntervalMs);
    }
}
