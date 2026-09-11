using Hilke.Ant.Plus;
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
    }
}
