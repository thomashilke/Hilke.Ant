using Hilke.Ant;
using Hilke.Ant.Model;
using Hilke.Ant.Plus.Common;

namespace Hilke.Ant.Plus.BicyclePower;

/// <summary>Cycling Dynamics characteristics that can be individually enabled on a supporting sensor.</summary>
[Flags]
public enum CyclingDynamicsFeatures : byte
{
    /// <summary>No optional Cycling Dynamics characteristics are enabled.</summary>
    None = 0,
    /// <summary>Enables the Right/Left Pedal Force Angle pages (0xE0/0xE1).</summary>
    PowerPhase = 0x08,
    /// <summary>Enables Platform Center Offset on the Pedal Position page (0xE2).</summary>
    PlatformCenterOffset = 0x10,
    /// <summary>Enables Rider Position on the Pedal Position page (0xE2).</summary>
    RiderPosition = 0x20,
    /// <summary>Enables the Torque Barycenter page (0x14).</summary>
    TorqueBarycenter = 0x40,
}

/// <summary>Outcome of an <see cref="BicyclePowerMonitor.EnableCyclingDynamicsAsync"/> call.</summary>
public enum CyclingDynamicsResult
{
    /// <summary>The sensor never responded to the capability query, or supports none of the requested features.</summary>
    Unsupported,
    /// <summary>Every requested feature was supported and enabled.</summary>
    Enabled,
    /// <summary>Only some of the requested features were supported; those were enabled.</summary>
    PartiallyEnabled,
    /// <summary>The capability query timed out before a response arrived.</summary>
    TimedOut,
}

/// <summary>Result of enabling Cycling Dynamics: what the sensor claims to support vs. what was actually enabled.</summary>
public readonly record struct CyclingDynamicsCapabilities(
    CyclingDynamicsResult Result,
    CyclingDynamicsFeatures Supported,
    CyclingDynamicsFeatures Enabled);

/// <summary>Decoded Subpage 0xFE (Advanced Capabilities 2) response from a power sensor.</summary>
internal readonly record struct AdvancedCapabilities2Response(byte CapabilitiesMask, byte CapabilitiesValue, DateTimeOffset At);

internal sealed class AdvancedCapabilities2Decoder : IDataPageDecoder<AdvancedCapabilities2Response>
{
    public const byte Page = 0x02;
    public const byte Subpage = 0xFE;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out AdvancedCapabilities2Response reading)
    {
        reading = default;
        if (payload8.Length < 8 || (payload8[0] & 0x7F) != Page || payload8[1] != Subpage)
            return false;
        reading = new AdvancedCapabilities2Response(payload8[4], payload8[6], DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>Right (0xE0) or Left (0xE1) Pedal Force Angle page.</summary>
public enum PedalSide
{
    /// <summary>The right pedal.</summary>
    Right,
    /// <summary>The left pedal.</summary>
    Left,
}

/// <summary>A decoded ANT+ Pedal Force Angle (Power Phase) page (0xE0/0xE1).</summary>
public readonly record struct PedalForceAngleReading(
    PedalSide Side,
    byte EventCount,
    double? StartAngleDegrees,
    double? EndAngleDegrees,
    double? StartPeakAngleDegrees,
    double? EndPeakAngleDegrees,
    double TorqueNewtonMeters,
    byte PageNumber,
    DateTimeOffset At) : IAntPlusDataPage;

internal sealed class PedalForceAngleDecoder : IDataPageDecoder<PedalForceAngleReading>
{
    public const byte RightPage = 0xE0;
    public const byte LeftPage = 0xE1;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out PedalForceAngleReading reading)
    {
        reading = default;
        if (payload8.Length < 8)
            return false;
        byte page = payload8[0];
        if (page != RightPage && page != LeftPage)
            return false;

        byte eventCount = payload8[1];
        byte startRaw = payload8[2], endRaw = payload8[3], startPeakRaw = payload8[4], endPeakRaw = payload8[5];
        static double Brads(byte v) => v * 360.0 / 256.0;

        double? start, end;
        if (startRaw == 0xC0 && endRaw == 0xC0) { start = null; end = null; }
        else { start = Brads(startRaw); end = Brads(endRaw); }

        double? startPeak, endPeak;
        if (startPeakRaw == 0xC0 && endPeakRaw == 0xC0) { startPeak = null; endPeak = null; }
        else { startPeak = Brads(startPeakRaw); endPeak = Brads(endPeakRaw); }

        double torque = (ushort)(payload8[6] | (payload8[7] << 8)) / 32.0;
        reading = new PedalForceAngleReading(page == RightPage ? PedalSide.Right : PedalSide.Left,
            eventCount, start, end, startPeak, endPeak, torque, page, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>Rider's body position on the bicycle, from the Pedal Position page (0xE2).</summary>
public enum RiderPosition
{
    /// <summary>Rider is seated.</summary>
    Seated,
    /// <summary>Rider is transitioning from standing to seated.</summary>
    TransitionToSeated,
    /// <summary>Rider is standing.</summary>
    Standing,
    /// <summary>Rider is transitioning from seated to standing.</summary>
    TransitionToStanding,
}

/// <summary>A decoded ANT+ Pedal Position page (0xE2): rider position, cadence, and Platform Center Offset.</summary>
public readonly record struct PedalPositionReading(
    byte EventCount,
    RiderPosition Position,
    byte? CadenceRpm,
    sbyte? RightPlatformCenterOffsetMm,
    sbyte? LeftPlatformCenterOffsetMm,
    byte PageNumber,
    DateTimeOffset At) : IAntPlusDataPage;

internal sealed class PedalPositionDecoder : IDataPageDecoder<PedalPositionReading>
{
    public const byte Page = 0xE2;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out PedalPositionReading reading)
    {
        reading = default;
        if (payload8.Length < 8 || payload8[0] != Page)
            return false;

        byte eventCount = payload8[1];
        var position = (RiderPosition)((payload8[2] >> 6) & 0x03);
        byte cadenceRaw = payload8[3];
        byte? cadence = cadenceRaw == 0xFF ? null : cadenceRaw;
        sbyte rightRaw = unchecked((sbyte)payload8[4]);
        sbyte leftRaw = unchecked((sbyte)payload8[5]);
        sbyte? rightPco = rightRaw == -128 ? null : rightRaw;
        sbyte? leftPco = leftRaw == -128 ? null : leftRaw;

        reading = new PedalPositionReading(eventCount, position, cadence, rightPco, leftPco, Page, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>A decoded ANT+ Torque Barycenter page (0x14): pedal-stroke torque application angle.</summary>
public readonly record struct TorqueBarycenterReading(double AngleDegrees, byte PageNumber, DateTimeOffset At) : IAntPlusDataPage;

internal sealed class TorqueBarycenterDecoder : IDataPageDecoder<TorqueBarycenterReading>
{
    public const byte Page = 0x14;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out TorqueBarycenterReading reading)
    {
        reading = default;
        if (payload8.Length < 8 || (payload8[0] & 0x7F) != Page)
            return false;
        double angle = 30.0 + payload8[1] * 0.5;
        reading = new TorqueBarycenterReading(angle, Page, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>
/// Drives the Cycling Dynamics enable handshake: queries Advanced Capabilities 2 (Get/Set Parameters
/// page 0x02, subpage 0xFE), then sets the requested features. Mirrors
/// <see cref="PowerMeterCalibrationSession"/>'s request/response shape but has no dedicated response for
/// the Set step (the spec defines none - success is observed only by pages 0xE0/0xE1/0xE2/0x14
/// subsequently appearing).
/// </summary>
internal sealed class CyclingDynamicsCapabilityQuery
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1.5);

    private readonly AntChannel _channel;
    private readonly AdvancedCapabilities2Decoder _decoder = new();
    private readonly object _gate = new();
    private TaskCompletionSource<AdvancedCapabilities2Response>? _pending;

    internal CyclingDynamicsCapabilityQuery(AntChannel channel) => _channel = channel;

    // Common Page 70 (Request Data Page): request subpage 0xFE of page 0x02, 4 broadcast responses.
    internal static byte[] BuildCapabilityRequest() =>
        new byte[] { 0x46, 0xFF, 0xFF, AdvancedCapabilities2Decoder.Subpage, 0xFF, 4, AdvancedCapabilities2Decoder.Page, 0x01 };

    // mask/value bit layout (bits 3-6 = features, bit 1 = 8Hz, bit 0 = 4Hz, bits 2/7 reserved=1):
    // 0 in mask = "apply this bit"; 0 in value = "enable this bit". Only 8Hz + requested features are
    // applied; everything else (incl. 4Hz) is left at "ignore" (mask bit = 1).
    internal static byte[] BuildSetRequest(CyclingDynamicsFeatures toEnable)
    {
        byte applyBits = (byte)(0x02 | (byte)toEnable); // bit1 = 8Hz, bits3-6 = requested features
        byte encoded = (byte)(~applyBits | 0x84); // 0x84 = reserved bits 2 and 7, forced to 1
        return new byte[] { AdvancedCapabilities2Decoder.Page, AdvancedCapabilities2Decoder.Subpage, 0xFF, 0xFF, encoded, 0xFF, encoded, 0xFF };
    }

    internal async Task<AdvancedCapabilities2Response?> QueryAsync(TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AdvancedCapabilities2Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pending = tcs;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
        try
        {
            // A single Common Page 70 request can be dropped by the RF link or arrive while the
            // sensor is mid-transmission of an unrelated page; real head units re-issue the request
            // periodically until a response arrives or the overall timeout elapses.
            while (true)
            {
                try { await _channel.SendAcknowledgedAsync(BuildCapabilityRequest(), ct).ConfigureAwait(false); }
                catch (AntCommandException) { return null; }

                var delay = Task.Delay(RetryInterval, cts.Token);
                var winner = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
                if (winner == tcs.Task)
                    return await tcs.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return null; }
        finally { lock (_gate) _pending = null; }
    }

    internal Task SendSetAsync(CyclingDynamicsFeatures toEnable, CancellationToken ct) =>
        _channel.SendAcknowledgedAsync(BuildSetRequest(toEnable), ct);

    /// <summary>
    /// Decode an Advanced Capabilities 2 page if present, resolving any pending <see cref="QueryAsync"/>
    /// and returning whether the page was recognized - regardless of whether a query is pending, so the
    /// owning monitor can fold this into its "did any decoder recognize this page" tracking (the sensor
    /// may also interleave this page unsolicited per spec section 15.2.5).
    /// </summary>
    internal bool HandleData(ReadOnlySpan<byte> payload8)
    {
        if (!_decoder.TryDecode(payload8, out var resp))
            return false;
        TaskCompletionSource<AdvancedCapabilities2Response>? tcs;
        lock (_gate) { tcs = _pending; }
        tcs?.TrySetResult(resp);
        return true;
    }
}
