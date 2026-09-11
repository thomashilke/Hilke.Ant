using Hilke.Ant.Plus.Common;
using Xunit;

namespace Hilke.Ant.Tests;

public class CommonDataPageTests
{
    [Fact]
    public void Battery_DecodesStatusVoltageAndOperatingTime()
    {
        var decoder = new BatteryStatusDecoder();
        // desc 0x35: coarse nibble 5, status (0x35>>4 & 7)==3 (Ok), res bit7=0 -> 16 s units.
        byte[] page = { 0x52, 0xFF, 0x01, 0x10, 0x00, 0x00, 0x80, 0x35 };

        Assert.True(decoder.TryDecode(page, out var reading));
        Assert.Equal(BatteryStatus.Ok, reading.Status);
        Assert.Equal(0x01, reading.BatteryId);
        Assert.Equal(5.5, reading.Voltage!.Value, 3); // coarse 5 + 0x80/256
        Assert.Equal(TimeSpan.FromSeconds(16 * 0x10), reading.CumulativeOperatingTime);
    }

    [Fact]
    public void Battery_InvalidCoarseVoltage_YieldsNull()
    {
        var decoder = new BatteryStatusDecoder();
        byte[] page = { 0x52, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x00, 0x0F }; // coarse nibble 0x0F -> invalid

        Assert.True(decoder.TryDecode(page, out var reading));
        Assert.Null(reading.Voltage);
    }

    [Fact]
    public void Manufacturer_DecodesFields()
    {
        var decoder = new ManufacturerInfoDecoder();
        byte[] page = { 0x50, 0xFF, 0xFF, 0x02, 0x0F, 0x00, 0x34, 0x12 };

        Assert.True(decoder.TryDecode(page, out var reading));
        Assert.Equal(0x02, reading.HardwareRevision);
        Assert.Equal(0x000F, reading.ManufacturerId);
        Assert.Equal(0x1234, reading.ModelNumber);
    }

    [Fact]
    public void Product_DecodesFields()
    {
        var decoder = new ProductInfoDecoder();
        byte[] page = { 0x51, 0xFF, 0xFF, 0x07, 0x78, 0x56, 0x34, 0x12 };

        Assert.True(decoder.TryDecode(page, out var reading));
        Assert.Equal(0x07, reading.SoftwareRevision);
        Assert.Equal(0x12345678u, reading.SerialNumber);
    }

    [Fact]
    public void WrongPageNumber_Rejected()
    {
        var decoder = new BatteryStatusDecoder();
        byte[] page = { 0x10, 0xFF, 0x01, 0x10, 0x00, 0x00, 0x80, 0x35 };

        Assert.False(decoder.TryDecode(page, out _));
    }
}
