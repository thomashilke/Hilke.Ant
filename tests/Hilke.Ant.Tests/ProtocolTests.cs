using Hilke.Ant.Protocol;
using Hilke.Ant.Protocol.Messages;
using Xunit;

namespace Hilke.Ant.Tests;

public class ProtocolTests
{
    [Fact]
    public void FrameRoundtrip_RecoversIdAndPayload()
    {
        byte[] data = { 0x00, 0x10, 0x01 };
        byte[] frame = AntFrame.Encode(AntMessageId.AssignChannel, data);

        Assert.Equal(AntConstants.Sync, frame[0]);
        Assert.Equal((byte)data.Length, frame[1]);
        Assert.Equal((byte)AntMessageId.AssignChannel, frame[2]);

        var parser = new AntFrameParser();
        parser.Append(frame);
        Assert.True(parser.TryReadMessage(out var msg));
        Assert.Equal(AntMessageId.AssignChannel, msg.Id);
        Assert.Equal(data, msg.Payload.ToArray());
        Assert.False(parser.TryReadMessage(out _));
    }

    [Fact]
    public void CorruptedChecksum_IsRejected()
    {
        byte[] frame = AntFrame.Encode(AntMessageId.OpenChannel, new byte[] { 0x00 });
        frame[^1] ^= 0xFF; // break checksum

        var parser = new AntFrameParser();
        parser.Append(frame);
        Assert.False(parser.TryReadMessage(out _));
        Assert.True(parser.DiscardedBytes > 0);
    }

    [Fact]
    public void PartialAppends_ByteByByte_Reassemble()
    {
        byte[] frame = AntFrame.Encode(AntMessageId.ChannelId,
            new byte[] { 0x00, 0x34, 0x12, 0x78, 0x01 });

        var parser = new AntFrameParser();
        for (int i = 0; i < frame.Length - 1; i++)
        {
            parser.Append(frame.AsSpan(i, 1));
            Assert.False(parser.TryReadMessage(out _));
        }
        parser.Append(frame.AsSpan(frame.Length - 1, 1));
        Assert.True(parser.TryReadMessage(out var msg));
        Assert.Equal(AntMessageId.ChannelId, msg.Id);
    }
    [Fact]
    public void PartialAppends_SplitInTwo_Reassemble()
    {
        byte[] frame = AntFrame.Encode(AntMessageId.ChannelId,
            new byte[] { 0x00, 0x34, 0x12, 0x78, 0x01 });

        var parser = new AntFrameParser();
        parser.Append(frame.AsSpan(0, 3));
        Assert.False(parser.TryReadMessage(out _));
        parser.Append(frame.AsSpan(3));
        Assert.True(parser.TryReadMessage(out var msg));
        Assert.Equal(AntMessageId.ChannelId, msg.Id);
    }

    [Fact]
    public void GarbagePrefix_ResynchronizesToNextSync()
    {
        byte[] frame = AntFrame.Encode(AntMessageId.OpenChannel, new byte[] { 0x02 });
        byte[] noise = { 0x00, 0x11, 0x22, 0x33 };
        byte[] stream = new byte[noise.Length + frame.Length];
        noise.CopyTo(stream, 0);
        frame.CopyTo(stream, noise.Length);

        var parser = new AntFrameParser();
        parser.Append(stream);
        Assert.True(parser.TryReadMessage(out var msg));
        Assert.Equal(AntMessageId.OpenChannel, msg.Id);
        Assert.Equal(new byte[] { 0x02 }, msg.Payload.ToArray());
    }

    [Fact]
    public void ExtendedBroadcast_ParsesChannelIdAndRssi()
    {
        // channel + 8 payload + flags(0xC0) + channelId(4) + rssi(3)
        byte[] data =
        {
            0x00, 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18,
            0xC0,
            0x34, 0x12, 0x78, 0x01,
            0x10, 0xC4, 0x00, // rssi value 0xC4 => -60
        };
        byte[] frame = AntFrame.Encode(AntMessageId.BroadcastData, data);

        var parser = new AntFrameParser();
        parser.Append(frame);
        Assert.True(parser.TryReadMessage(out var msg));
        var rx = InboundMessages.ParseReceivedData(msg);

        Assert.NotNull(rx.ExtendedId);
        Assert.Equal((ushort)0x1234, rx.ExtendedId!.Value.DeviceNumber);
        Assert.Equal(0x78, rx.ExtendedId.Value.DeviceType);
        Assert.Equal(0x01, rx.ExtendedId.Value.TransmissionType);
        Assert.Equal((sbyte)-60, rx.Rssi);
    }
}
