using Hilke.Ant.Plus.BicyclePower;
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

    [Fact]
    public void TorqueEffectiveness_DecodesSplitLeftRight()
    {
        var decoder = new TorqueEffectivenessDecoder();
        byte[] page = { 0x13, 0x05, 0x64, 0x78, 0x50, 0x46, 0xFF, 0xFF };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Equal(50.0, r.LeftTorqueEffectivenessPercent);
        Assert.Equal(60.0, r.RightTorqueEffectivenessPercent);
        Assert.Equal(40.0, r.LeftPedalSmoothnessPercent);
        Assert.Equal(35.0, r.RightPedalSmoothnessPercent);
        Assert.Null(r.CombinedPedalSmoothnessPercent);
    }

    [Fact]
    public void TorqueEffectiveness_DecodesCombinedPedalSmoothness()
    {
        var decoder = new TorqueEffectivenessDecoder();
        byte[] page = { 0x13, 0x05, 0x64, 0x78, 0x64, 0xFE, 0xFF, 0xFF };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Equal(50.0, r.CombinedPedalSmoothnessPercent);
        Assert.Null(r.LeftPedalSmoothnessPercent);
        Assert.Null(r.RightPedalSmoothnessPercent);
    }

    [Fact]
    public void TorqueEffectiveness_TreatsFFAsInvalid()
    {
        var decoder = new TorqueEffectivenessDecoder();
        byte[] page = { 0x13, 0x05, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Null(r.LeftTorqueEffectivenessPercent);
        Assert.Null(r.RightTorqueEffectivenessPercent);
        Assert.Null(r.LeftPedalSmoothnessPercent);
        Assert.Null(r.RightPedalSmoothnessPercent);
    }

    [Fact]
    public void TorqueEffectiveness_WrongPage_IsRejected()
    {
        var decoder = new TorqueEffectivenessDecoder();
        Assert.False(decoder.TryDecode(new byte[] { 0x10, 0, 0, 0, 0, 0, 0, 0 }, out _));
    }

    [Fact]
    public void PedalForceAngle_DecodesAnglesAndTorque()
    {
        var decoder = new PedalForceAngleDecoder();
        byte[] page = { 0xE0, 3, 0x10, 0x80, 0x08, 0x40, 0x40, 0x00 };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Equal(PedalSide.Right, r.Side);
        Assert.Equal(22.5, r.StartAngleDegrees);
        Assert.Equal(180.0, r.EndAngleDegrees);
        Assert.Equal(11.25, r.StartPeakAngleDegrees);
        Assert.Equal(90.0, r.EndPeakAngleDegrees);
        Assert.Equal(2.0, r.TorqueNewtonMeters);
    }

    [Fact]
    public void PedalForceAngle_TreatsC0PairAsInvalid()
    {
        var decoder = new PedalForceAngleDecoder();
        byte[] page = { 0xE1, 3, 0xC0, 0xC0, 0xC0, 0xC0, 0x40, 0x00 };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Equal(PedalSide.Left, r.Side);
        Assert.Null(r.StartAngleDegrees);
        Assert.Null(r.EndAngleDegrees);
        Assert.Null(r.StartPeakAngleDegrees);
        Assert.Null(r.EndPeakAngleDegrees);
    }

    [Fact]
    public void PedalPosition_DecodesRiderPositionCadenceAndPco()
    {
        var decoder = new PedalPositionDecoder();
        byte[] page = { 0xE2, 1, 0x80, 60, 5, unchecked((byte)-3), 0xFF, 0xFF };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Equal(RiderPosition.Standing, r.Position);
        Assert.Equal((byte)60, r.CadenceRpm);
        Assert.Equal((sbyte)5, r.RightPlatformCenterOffsetMm);
        Assert.Equal((sbyte)-3, r.LeftPlatformCenterOffsetMm);
    }

    [Fact]
    public void PedalPosition_TreatsSentinelsAsInvalid()
    {
        var decoder = new PedalPositionDecoder();
        byte[] page = { 0xE2, 1, 0x00, 0xFF, 0x80, 0x80, 0xFF, 0xFF };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Null(r.CadenceRpm);
        Assert.Null(r.RightPlatformCenterOffsetMm);
        Assert.Null(r.LeftPlatformCenterOffsetMm);
    }

    [Fact]
    public void TorqueBarycenter_DecodesAngle()
    {
        var decoder = new TorqueBarycenterDecoder();
        byte[] page = { 0x14, 0x64, 0, 0, 0, 0, 0, 0 };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Equal(80.0, r.AngleDegrees);
    }

    [Fact]
    public void AdvancedCapabilities2_MaskValueRoundTrip()
    {
        byte[] setRequest = CyclingDynamicsCapabilityQuery.BuildSetRequest(CyclingDynamicsFeatures.PowerPhase);
        Assert.Equal((byte)0xF5, setRequest[4]);
        Assert.Equal((byte)0xF5, setRequest[6]);

        var decoder = new AdvancedCapabilities2Decoder();
        byte[] responsePage = { AdvancedCapabilities2Decoder.Page, AdvancedCapabilities2Decoder.Subpage, 0xFF, 0xFF, setRequest[4], 0xFF, setRequest[6], 0xFF };
        Assert.True(decoder.TryDecode(responsePage, out var resp));
        var supported = (CyclingDynamicsFeatures)(~resp.CapabilitiesMask & 0x78);
        Assert.Equal(CyclingDynamicsFeatures.PowerPhase, supported);
    }
}
