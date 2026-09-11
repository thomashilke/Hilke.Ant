using Hilke.Ant;
using Hilke.Ant.Protocol;
using Hilke.Ant.Model;
using Hilke.Ant.Plus;
using Hilke.Ant.Testing;
using Xunit;

namespace Hilke.Ant.Tests;

public class FitnessEquipmentTests
{
    [Fact]
    public void GeneralFeData_DecodesSpeedHeartRateAndState()
    {
        var decoder = new GeneralFitnessDataDecoder();
        // page 0x10, type 25(bike), elapsed 8 (2.0s), distance 100m, speed 5000mm/s(5 m/s),
        // hr 150, flags 0x34 -> state (0x3=InUse), capabilities 0x4
        byte[] page = { 0x10, 25, 8, 100, 0x88, 0x13, 150, 0x34 };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Equal((byte)25, r.EquipmentType);
        Assert.Equal(TimeSpan.FromSeconds(2.0), r.ElapsedTime);
        Assert.Equal((byte)100, r.DistanceMeters);
        Assert.Equal(5.0, r.SpeedMetersPerSecond!.Value, 3);
        Assert.Equal((byte)150, r.HeartRate);
        Assert.Equal(FitnessEquipmentState.InUse, r.State);
        Assert.Equal((byte)0x04, r.Capabilities);
    }

    [Fact]
    public void GeneralFeData_InvalidSpeedAndHeartRate_AreNull()
    {
        var decoder = new GeneralFitnessDataDecoder();
        byte[] page = { 0x10, 25, 0, 0, 0xFF, 0xFF, 0xFF, 0x20 };
        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Null(r.SpeedMetersPerSecond);
        Assert.Null(r.HeartRate);
        Assert.Equal(FitnessEquipmentState.Ready, r.State);
    }

    [Fact]
    public void TrainerData_Decodes12BitInstantaneousPower()
    {
        var decoder = new TrainerDataDecoder();
        // page 0x19, event 3, cadence 85, accum 0x0190(400), inst power 0x123(291),
        // byte6 = (trainerStatus<<4)|(power MSB nibble) = (0x2<<4)|0x1 = 0x21, byte7 flags 0x30 -> state InUse
        byte[] page = { 0x19, 3, 85, 0x90, 0x01, 0x23, 0x21, 0x30 };

        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Equal((byte)3, r.EventCount);
        Assert.Equal((byte)85, r.Cadence);
        Assert.Equal((ushort)400, r.AccumulatedPower);
        Assert.Equal((ushort)0x123, r.InstantaneousPower);
        Assert.Equal((byte)0x2, r.TrainerStatus);
        Assert.Equal(FitnessEquipmentState.InUse, r.State);
    }

    [Fact]
    public void TrainerData_InvalidPowerAndCadence_AreNull()
    {
        var decoder = new TrainerDataDecoder();
        // inst power = 0x0FFF invalid: byte5=0xFF, byte6 low nibble=0x0F -> byte6=0x0F
        byte[] page = { 0x19, 0, 0xFF, 0x00, 0x00, 0xFF, 0x0F, 0x00 };
        Assert.True(decoder.TryDecode(page, out var r));
        Assert.Null(r.Cadence);
        Assert.Null(r.InstantaneousPower);
    }

    [Fact]
    public void TargetPowerPage_EncodesQuarterWattUnits()
    {
        byte[] page = FitnessEquipmentMonitor.BuildTargetPowerPage(200);
        // 200 W * 4 = 800 = 0x0320 LE at bytes 6,7
        Assert.Equal(0x31, page[0]);
        Assert.Equal(0x20, page[6]);
        Assert.Equal(0x03, page[7]);
    }

    [Fact]
    public void BasicResistancePage_EncodesHalfPercentUnits()
    {
        byte[] page = FitnessEquipmentMonitor.BuildBasicResistancePage(50); // 50% -> 100 units
        Assert.Equal(0x30, page[0]);
        Assert.Equal((byte)100, page[7]);
    }

    [Fact]
    public async Task SetTargetPower_SendsAcknowledgedControlPage()
    {
        var transport = new InMemoryAntTransport();
        var sim = new SimulatedAntRadio(transport);
        var device = new AntDevice(transport) { CommandTimeout = TimeSpan.FromMilliseconds(300) };
        await using (device)
        await using (sim)
        {
            await device.OpenAsync();
            var channel = await device.ConfigureChannelAsync(0, FitnessEquipmentMonitor.SlaveDefaults());
            await channel.OpenAsync();
            var fe = new FitnessEquipmentMonitor(channel);

            var send = fe.SetTargetPowerAsync(250);
            sim.InjectEvent(0, ChannelResponseCode.EventTransferTxCompleted);
            await send;

            Assert.NotNull(sim.LastAcknowledgedPage);
            Assert.Equal(FitnessEquipmentMonitor.BuildTargetPowerPage(250), sim.LastAcknowledgedPage);
        }
    }
}
