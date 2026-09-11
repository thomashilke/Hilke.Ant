namespace Hilke.Ant.Plus.Common;

/// <summary>ANT+ common battery status (page 0x52, descriptor high nibble).</summary>
public enum BatteryStatus : byte
{
    /// <summary>No battery status reported.</summary>
    Unknown = 0,
    /// <summary>Battery is new.</summary>
    New = 1,
    /// <summary>Battery charge is good.</summary>
    Good = 2,
    /// <summary>Battery charge is ok.</summary>
    Ok = 3,
    /// <summary>Battery charge is low.</summary>
    Low = 4,
    /// <summary>Battery charge is critical.</summary>
    Critical = 5,
    /// <summary>Reserved value.</summary>
    Reserved = 6,
    /// <summary>The status field is invalid.</summary>
    Invalid = 7,
}

/// <summary>ANT+ common Battery Status data page (0x52).</summary>
public readonly record struct BatteryStatusPage(
    byte BatteryId,
    BatteryStatus Status,
    double? Voltage,
    TimeSpan CumulativeOperatingTime,
    byte PageNumber,
    DateTimeOffset At) : IAntPlusDataPage;

/// <summary>Decoder for the ANT+ common Battery Status page (0x52).</summary>
internal sealed class BatteryStatusDecoder : IDataPageDecoder<BatteryStatusPage>
{
    /// <summary>Common Battery Status page number.</summary>
    public const byte Page = 0x52;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out BatteryStatusPage reading)
    {
        reading = default;
        if (payload8.Length < 8 || (payload8[0] & 0x7F) != Page)
            return false;

        byte batteryId = payload8[2];
        uint cot = (uint)(payload8[3] | (payload8[4] << 8) | (payload8[5] << 16));
        byte desc = payload8[7];
        byte coarse = (byte)(desc & 0x0F);
        var status = (BatteryStatus)((desc >> 4) & 0x07);
        bool res2s = (desc & 0x80) != 0;
        double? voltage = coarse == 0x0F ? null : coarse + payload8[6] / 256.0;
        var operating = TimeSpan.FromSeconds(cot * (res2s ? 2.0 : 16.0));

        reading = new BatteryStatusPage(batteryId, status, voltage, operating, (byte)Page, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>ANT+ common Manufacturer's Information data page (0x50).</summary>
public readonly record struct ManufacturerInfoPage(
    byte HardwareRevision,
    ushort ManufacturerId,
    ushort ModelNumber,
    byte PageNumber,
    DateTimeOffset At) : IAntPlusDataPage;

/// <summary>Decoder for the ANT+ common Manufacturer's Information page (0x50).</summary>
internal sealed class ManufacturerInfoDecoder : IDataPageDecoder<ManufacturerInfoPage>
{
    /// <summary>Common Manufacturer's Information page number.</summary>
    public const byte Page = 0x50;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out ManufacturerInfoPage reading)
    {
        reading = default;
        if (payload8.Length < 8 || (payload8[0] & 0x7F) != Page)
            return false;

        byte hardwareRevision = payload8[3];
        ushort manufacturerId = (ushort)(payload8[4] | (payload8[5] << 8));
        ushort modelNumber = (ushort)(payload8[6] | (payload8[7] << 8));

        reading = new ManufacturerInfoPage(hardwareRevision, manufacturerId, modelNumber, (byte)Page, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>ANT+ common Product Information data page (0x51).</summary>
public readonly record struct ProductInfoPage(
    byte SoftwareRevision,
    uint SerialNumber,
    byte PageNumber,
    DateTimeOffset At) : IAntPlusDataPage;

/// <summary>Decoder for the ANT+ common Product Information page (0x51).</summary>
internal sealed class ProductInfoDecoder : IDataPageDecoder<ProductInfoPage>
{
    /// <summary>Common Product Information page number.</summary>
    public const byte Page = 0x51;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out ProductInfoPage reading)
    {
        reading = default;
        if (payload8.Length < 8 || (payload8[0] & 0x7F) != Page)
            return false;

        byte softwareRevision = payload8[3];
        uint serialNumber = (uint)(payload8[4] | (payload8[5] << 8) | (payload8[6] << 16) | (payload8[7] << 24));

        reading = new ProductInfoPage(softwareRevision, serialNumber, (byte)Page, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>Shared stateless common-page decoders, dispatched together against every message.</summary>
internal static class CommonDataPageDecoders
{
    private static readonly BatteryStatusDecoder Battery = new();
    private static readonly ManufacturerInfoDecoder Manufacturer = new();
    private static readonly ProductInfoDecoder Product = new();

    internal static void TryDispatch(
        ReadOnlySpan<byte> page8,
        Action<BatteryStatusPage> onBattery,
        Action<ManufacturerInfoPage> onManufacturer,
        Action<ProductInfoPage> onProduct)
    {
        if (Battery.TryDecode(page8, out var battery)) onBattery(battery);
        if (Manufacturer.TryDecode(page8, out var manufacturer)) onManufacturer(manufacturer);
        if (Product.TryDecode(page8, out var product)) onProduct(product);
    }
}
