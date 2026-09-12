using Hilke.Ant.Plus.Common;

namespace Hilke.Ant.Plus;

/// <summary>
/// Sparse decoded-fields snapshot: one shape reused by both scan-time device sightings
/// (<see cref="AntPlusDeviceSighting"/>) and connected-profile telemetry
/// (<see cref="IAntPlusProfileConnection.TelemetryUpdated"/>). Only the fields relevant to the
/// page(s) just decoded are set; everything else is null.
/// </summary>
public sealed record AntPlusTelemetryUpdate
{
    /// <summary>Heart rate in beats per minute, when a heart-rate page was just decoded.</summary>
    public int? HeartRate { get; init; }
    /// <summary>Instantaneous power in watts, when a power page was just decoded.</summary>
    public int? PowerWatts { get; init; }
    /// <summary>Power averaged across messages (watts), when a power page provided enough history.</summary>
    public double? AveragePower { get; init; }
    /// <summary>Cadence in RPM, when a page carrying cadence was just decoded.</summary>
    public int? Cadence { get; init; }
    /// <summary>Speed in meters per second, when an FE-C general data page was just decoded.</summary>
    public double? SpeedMps { get; init; }
    /// <summary>FE-C trainer status flags, formatted as a hex string, when a trainer data page was just decoded.</summary>
    public string? TrainerStatus { get; init; }
    /// <summary>Battery status, when a common Battery Status page was just decoded.</summary>
    public BatteryStatus? Battery { get; init; }
    /// <summary>Battery voltage, when a common Battery Status page was just decoded.</summary>
    public double? BatteryVolts { get; init; }
    /// <summary>Manufacturer info, when a common Manufacturer's Information page was just decoded.</summary>
    public ManufacturerInfoPage? Manufacturer { get; init; }
    /// <summary>Product info, when a common Product Information page was just decoded.</summary>
    public ProductInfoPage? Product { get; init; }
    /// <summary>Heart rate variability (R-R interval) in milliseconds, when a page carrying it was just decoded.</summary>
    public int? RrIntervalMs { get; init; }
    /// <summary>Left-leg torque effectiveness percentage, when a Torque Effectiveness and Pedal Smoothness page was just decoded.</summary>
    public double? LeftTorqueEffectivenessPercent { get; init; }
    /// <summary>Right-leg torque effectiveness percentage, when a Torque Effectiveness and Pedal Smoothness page was just decoded.</summary>
    public double? RightTorqueEffectivenessPercent { get; init; }
    /// <summary>Left-leg (or combined) pedal smoothness percentage, when a Torque Effectiveness and Pedal Smoothness page was just decoded.</summary>
    public double? LeftPedalSmoothnessPercent { get; init; }
    /// <summary>Right-leg pedal smoothness percentage, when a Torque Effectiveness and Pedal Smoothness page was just decoded.</summary>
    public double? RightPedalSmoothnessPercent { get; init; }
    /// <summary>Combined pedal smoothness percentage, when the sensor reports a single combined value instead of left/right.</summary>
    public double? CombinedPedalSmoothnessPercent { get; init; }
}
