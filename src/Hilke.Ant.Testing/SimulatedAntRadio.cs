using Hilke.Ant.Model;
using Hilke.Ant.Protocol;
using Hilke.Ant.Protocol.Messages;

namespace Hilke.Ant.Testing;

/// <summary>
/// A deterministic ANT device double. Parses host frames, tracks per-channel device state,
/// answers reset/capabilities/config commands, and lets tests inject broadcasts and events.
/// </summary>
public sealed class SimulatedAntRadio : IAsyncDisposable
{
    private readonly InMemoryAntTransport _transport;
    private readonly AntFrameParser _parser = new();
    private readonly DeviceChannelState[] _channelStates;
    private readonly Dictionary<AntMessageId, Queue<ChannelResponseCode>> _forced = new();
    private readonly HashSet<AntMessageId> _swallow = new();
    private readonly object _gate = new();

    private readonly Task _loop;
    private readonly CancellationTokenSource _cts = new();
    private bool _extRxEnabled;
    private bool _disposed;

    public SimulatedAntRadio(InMemoryAntTransport transport, byte maxChannels = 8, byte maxNetworks = 3)
    {
        _transport = transport;
        MaxChannels = maxChannels;
        MaxNetworks = maxNetworks;
        _channelStates = new DeviceChannelState[maxChannels];
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public byte MaxChannels { get; }
    public byte MaxNetworks { get; }

    /// <summary>The 8-byte data page of the most recent acknowledged-data message from the host.</summary>
    public byte[]? LastAcknowledgedPage { get; private set; }

    /// <summary>Force the next command with <paramref name="id"/> to be answered with <paramref name="code"/>.</summary>
    public void FailNextCommand(AntMessageId id, ChannelResponseCode code)
    {
        lock (_gate)
        {
            if (!_forced.TryGetValue(id, out var q))
                _forced[id] = q = new Queue<ChannelResponseCode>();
            q.Enqueue(code);
        }
    }

    /// <summary>Silently drop the next command with <paramref name="id"/> (no reply) to exercise timeouts.</summary>
    public void SwallowNextCommand(AntMessageId id)
    {
        lock (_gate) { _swallow.Add(id); }
    }

    /// <summary>Emit a broadcast (0x4E) on <paramref name="channel"/>, extended when the host enabled ext-rx.</summary>
    public void InjectBroadcast(byte channel, ChannelId from, ReadOnlySpan<byte> page8, sbyte? rssi = null)
    {
        if (page8.Length != 8)
            throw new ArgumentException("Page must be 8 bytes.", nameof(page8));

        bool ext;
        lock (_gate) { ext = _extRxEnabled; }

        byte[] data;
        if (ext)
        {
            bool withRssi = rssi is not null;
            int len = 9 + 1 + 4 + (withRssi ? 3 : 0);
            data = new byte[len];
            data[0] = channel;
            page8.CopyTo(data.AsSpan(1, 8));
            byte flags = AntConstants.LibConfigChannelId;
            if (withRssi) flags |= AntConstants.LibConfigRssi;
            data[9] = flags;
            int o = 10;
            data[o++] = (byte)(from.DeviceNumber & 0xFF);
            data[o++] = (byte)(from.DeviceNumber >> 8);
            data[o++] = from.DeviceType;
            data[o++] = from.TransmissionType;
            if (withRssi)
            {
                data[o++] = 0x10;             // measurement type
                data[o++] = (byte)rssi!.Value; // value
                data[o++] = 0;                 // threshold config
            }
        }
        else
        {
            data = new byte[9];
            data[0] = channel;
            page8.CopyTo(data.AsSpan(1, 8));
        }

        _transport.SendToHost(AntFrame.Encode(AntMessageId.BroadcastData, data));
    }

    /// <summary>Emit an RF event (0x40 with responseToId=1) on <paramref name="channel"/>.</summary>
    public void InjectEvent(byte channel, ChannelResponseCode ev)
    {
        _transport.SendToHost(AntFrame.Encode(AntMessageId.ChannelResponseEvent,
            stackalloc byte[] { channel, AntConstants.EventResponseMarker, (byte)ev }));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _transport.HostFrames.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _parser.Append(frame);
                while (_parser.TryReadMessage(out var msg))
                    Handle(msg);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void Handle(AntMessage msg)
    {
        // Forced-error / swallow hooks.
        lock (_gate)
        {
            if (_swallow.Remove(msg.Id))
                return;
            if (_forced.TryGetValue(msg.Id, out var q) && q.Count > 0)
            {
                var code = q.Dequeue();
                byte fch = msg.Payload.Length > 0 ? msg.Payload.Span[0] : (byte)0;
                RespondLocked(fch, msg.Id, code);
                return;
            }
        }

        switch (msg.Id)
        {
            case AntMessageId.ResetSystem:
                _transport.SendToHost(AntFrame.Encode(AntMessageId.StartupMessage, stackalloc byte[] { 0x20 }));
                break;

            case AntMessageId.RequestMessage:
            {
                var p = msg.Payload.Span;
                var requested = p.Length > 1 ? (AntMessageId)p[1] : default;
                if (requested == AntMessageId.Capabilities)
                    _transport.SendToHost(AntFrame.Encode(AntMessageId.Capabilities,
                        stackalloc byte[] { MaxChannels, MaxNetworks, 0x00, 0x00 }));
                break;
            }

            case AntMessageId.EnableExtRxMessages:
            {
                var p = msg.Payload.Span;
                lock (_gate) { _extRxEnabled = p.Length > 1 && p[1] != 0; }
                Respond(Channel(msg), msg.Id, ChannelResponseCode.ResponseNoError);
                break;
            }

            case AntMessageId.LibConfig:
            {
                var p = msg.Payload.Span;
                if (p.Length > 1 && (p[1] & AntConstants.LibConfigChannelId) != 0)
                    lock (_gate) { _extRxEnabled = true; }
                Respond(Channel(msg), msg.Id, ChannelResponseCode.ResponseNoError);
                break;
            }

            case AntMessageId.AssignChannel:
                SetState(Channel(msg), DeviceChannelState.Assigned);
                Respond(Channel(msg), msg.Id, ChannelResponseCode.ResponseNoError);
                break;

            case AntMessageId.UnassignChannel:
                SetState(Channel(msg), DeviceChannelState.Unassigned);
                Respond(Channel(msg), msg.Id, ChannelResponseCode.ResponseNoError);
                break;

            case AntMessageId.OpenChannel:
            {
                byte ch = Channel(msg);
                if (StateOf(ch) == DeviceChannelState.Unassigned)
                {
                    Respond(ch, msg.Id, ChannelResponseCode.ChannelInWrongState);
                    break;
                }
                SetState(ch, DeviceChannelState.Searching);
                Respond(ch, msg.Id, ChannelResponseCode.ResponseNoError);
                break;
            }

            case AntMessageId.CloseChannel:
            {
                byte ch = Channel(msg);
                Respond(ch, msg.Id, ChannelResponseCode.ResponseNoError);
                SetState(ch, DeviceChannelState.Assigned);
                InjectEvent(ch, ChannelResponseCode.EventChannelClosed);
                break;
            }

            case AntMessageId.OpenRxScanMode:
                SetState(0, DeviceChannelState.Searching);
                Respond(0, msg.Id, ChannelResponseCode.ResponseNoError);
                break;

            // Pure config commands: acknowledge.
            case AntMessageId.ChannelId:
            case AntMessageId.RfFrequency:
            case AntMessageId.ChannelPeriod:
            case AntMessageId.SearchTimeout:
            case AntMessageId.NetworkKey:
            case AntMessageId.ChannelTransmitPower:
            case AntMessageId.TransmitPower:
                Respond(Channel(msg), msg.Id, ChannelResponseCode.ResponseNoError);
                break;

            // Master TX data messages carry no channel response; tests inject the transfer
            // outcome (EventTransferTxCompleted / EventTransferTxFailed) or EventTx explicitly.
            case AntMessageId.AcknowledgedData:
                if (msg.Payload.Length >= 9)
                    LastAcknowledgedPage = msg.Payload.Span.Slice(1, 8).ToArray();
                break;
            case AntMessageId.BroadcastData:
            case AntMessageId.BurstData:
                break;

            default:
                Respond(Channel(msg), msg.Id, ChannelResponseCode.ResponseNoError);
                break;
        }
    }

    private static byte Channel(AntMessage msg) => msg.Payload.Length > 0 ? msg.Payload.Span[0] : (byte)0;

    private DeviceChannelState StateOf(byte channel)
        => channel < _channelStates.Length ? _channelStates[channel] : DeviceChannelState.Unassigned;

    private void SetState(byte channel, DeviceChannelState state)
    {
        if (channel < _channelStates.Length)
            _channelStates[channel] = state;
    }

    private void Respond(byte channel, AntMessageId toId, ChannelResponseCode code)
        => _transport.SendToHost(AntFrame.Encode(AntMessageId.ChannelResponseEvent,
            stackalloc byte[] { channel, (byte)toId, (byte)code }));

    private void RespondLocked(byte channel, AntMessageId toId, ChannelResponseCode code)
        => Respond(channel, toId, code);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
