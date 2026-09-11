namespace Hilke.Ant.Plus;

/// <summary>
/// Sparse decoded-fields snapshot: one shape reused by both scan-time device sightings
/// (<see cref="AntPlusDeviceSighting"/>) and connected-profile telemetry
/// (<see cref="IAntPlusProfileConnection.TelemetryUpdated"/>). Only the fields relevant to the
/// page(s) just decoded are set; everything else is null.
/// </summary>
public sealed record AntPlusTelemetryUpdate
{
    public int? HeartRate { get; init; }
    public int? PowerWatts { get; init; }
    public double? AveragePower { get; init; }
    public int? Cadence { get; init; }
    public double? SpeedMps { get; init; }
    public string? TrainerStatus { get; init; }
    public BatteryStatus? Battery { get; init; }
    public double? BatteryVolts { get; init; }
    public ManufacturerInfoPage? Manufacturer { get; init; }
    public ProductInfoPage? Product { get; init; }
}
