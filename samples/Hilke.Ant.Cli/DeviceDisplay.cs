using Hilke.Ant.Plus;
using Hilke.Ant.Plus.HeartRate;
using Hilke.Ant.Plus.BicyclePower;
using Hilke.Ant.Plus.FitnessEquipment;

namespace Hilke.Ant.Cli;

internal static class DeviceDisplay
{
    public static string FormatPrimary(TrackedDeviceEntry e) => e.DeviceType switch
    {
        HeartRateMonitor.DeviceType => $"{Fmt(e.HeartRate)} bpm",
        BicyclePowerMonitor.DeviceType => $"{Fmt(e.PowerWatts)} W  {Fmt(e.Cadence)} rpm",
        FitnessEquipmentMonitor.DeviceType => $"{Fmt(e.PowerWatts)} W  {Fmt(e.Cadence)} rpm  {Fmt(e.SpeedMps)} m/s",
        _ => "--",
    };

    /// <summary>
    /// Human-readable channel status. Unlike the raw <see cref="AntPlusChannelState"/> label, this
    /// resolves the two real ambiguities a bare "Lost"/"Configured" state leaves open: whether the
    /// radio actually confirmed the loss and re-searched (vs. just a local "no data recently"
    /// heuristic), and whether a closed channel gave up on its own (search timeout) or was closed
    /// on request.
    /// </summary>
    public static string FormatState(TrackedDeviceEntry e)
    {
        if (!e.Connected)
            return "visible";
        return e.State switch
        {
            AntPlusChannelState.Unconfigured => "disconnected",
            AntPlusChannelState.Configured when e.LastTransitionReason == AntPlusChannelTransitionReason.SearchTimedOut => "timed out",
            AntPlusChannelState.Configured => "closed",
            AntPlusChannelState.Searching => "searching",
            AntPlusChannelState.Tracking => "tracking",
            AntPlusChannelState.Lost when e.LastTransitionReason == AntPlusChannelTransitionReason.DeviceLost => "re-search",
            AntPlusChannelState.Lost when e.LastTransitionReason == AntPlusChannelTransitionReason.InactivityTimeout => "stale",
            AntPlusChannelState.Lost => "lost",
            AntPlusChannelState.Closing => "closing",
            _ => e.State.ToString(),
        };
    }

    private static string Fmt(int? v) => v?.ToString() ?? "--";
    private static string Fmt(double? v) => v is { } d ? d.ToString("F1") : "--";
}
