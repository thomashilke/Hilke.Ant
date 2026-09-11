using System.Buffers;
using Hilke.Ant.Model;
using Hilke.Ant.Protocol;
using Hilke.Ant.Protocol.Messages;
using Hilke.Ant.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hilke.Ant;

/// <summary>
/// The ANT radio: owns the transport, a single background read loop, the write/command pipeline,
/// and the scan/channel mutual-exclusion guard.
/// </summary>
internal sealed class AntDevice : IAsyncDisposable
{
    private enum RadioMode { Idle, Channels, Scanning }

    private readonly IAntTransport _transport;
    private readonly ILogger _logger;
    private readonly AntFrameParser _parser = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly object _pendingLock = new();
    private readonly List<Pending> _pending = new();

    private AntChannel?[] _channels = Array.Empty<AntChannel>();
    private ScanSession? _scan;
    private int _openChannelCount;
    private RadioMode _mode = RadioMode.Idle;

    private Task? _readLoop;
    private CancellationTokenSource? _readCts;
    private bool _disposed;

    public AntDevice(IAntTransport transport, ILogger? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Per-command response timeout.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Timeout awaiting the startup message after reset.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(1);

    public AntCapabilities Capabilities { get; private set; } = AntCapabilities.Unknown;

    /// <summary>Open the transport, start the read loop, reset, and query capabilities.</summary>
    public async Task OpenAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_transport.IsOpen)
            await _transport.OpenAsync(ct).ConfigureAwait(false);

        _readCts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_readCts.Token));

        // Reset and await startup.
        await SendCommandAsync(OutboundMessages.ResetSystem(),
            m => m.Id == AntMessageId.StartupMessage, AntMessageId.ResetSystem, StartupTimeout, ct).ConfigureAwait(false);

        // Query capabilities.
        var capsMsg = await SendCommandAsync(OutboundMessages.RequestMessage(0, AntMessageId.Capabilities),
            m => m.Id == AntMessageId.Capabilities, AntMessageId.RequestMessage, CommandTimeout, ct).ConfigureAwait(false);
        var caps = InboundMessages.ParseCapabilities(capsMsg);
        Capabilities = new AntCapabilities(caps.MaxChannels, caps.MaxNetworks, caps.StandardOptions, caps.AdvancedOptions);
        _channels = new AntChannel?[Math.Max(1, (int)caps.MaxChannels)];
        _logger.LogInformation("ANT device opened: {MaxChannels} channels, {MaxNetworks} networks.", caps.MaxChannels, caps.MaxNetworks);
    }

    public Task SetNetworkKeyAsync(byte network, ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return SendConfigAsync(AntMessageId.NetworkKey, OutboundMessages.SetNetworkKey(network, key.Span), ct);
    }

    /// <summary>Configure a channel: assign, id, frequency, period, search timeout, ext-rx, tx power.</summary>
    public async Task<AntChannel> ConfigureChannelAsync(byte channelNumber, ChannelConfiguration cfg, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (cfg is null) throw new ArgumentNullException(nameof(cfg));
        lock (_pendingLock)
        {
            if (_mode == RadioMode.Scanning)
                throw new RadioBusyException("Cannot configure a channel while a scan session is active.");
        }
        if (channelNumber >= _channels.Length)
            throw new ArgumentOutOfRangeException(nameof(channelNumber), $"Channel {channelNumber} exceeds device max {_channels.Length}.");
        if (_channels[channelNumber] is not null)
            throw new InvalidOperationException($"Channel {channelNumber} is already configured.");

        await SendConfigAsync(AntMessageId.AssignChannel,
            OutboundMessages.AssignChannel(channelNumber, cfg.Type, cfg.NetworkNumber), ct).ConfigureAwait(false);
        await SendConfigAsync(AntMessageId.ChannelId,
            OutboundMessages.SetChannelId(channelNumber, cfg.ChannelId), ct).ConfigureAwait(false);
        await SendConfigAsync(AntMessageId.RfFrequency,
            OutboundMessages.SetRfFrequency(channelNumber, cfg.RfFrequency), ct).ConfigureAwait(false);
        await SendConfigAsync(AntMessageId.ChannelPeriod,
            OutboundMessages.SetChannelPeriod(channelNumber, cfg.ChannelPeriod), ct).ConfigureAwait(false);
        await SendConfigAsync(AntMessageId.SearchTimeout,
            OutboundMessages.SetSearchTimeout(channelNumber, cfg.SearchTimeout), ct).ConfigureAwait(false);
        if (cfg.UseExtendedMessages)
        {
            await SendConfigAsync(AntMessageId.LibConfig,
                OutboundMessages.LibConfig((byte)(AntConstants.LibConfigChannelId | AntConstants.LibConfigRssi)), ct).ConfigureAwait(false);
            await SendConfigAsync(AntMessageId.EnableExtRxMessages,
                OutboundMessages.EnableExtRxMessages(true), ct).ConfigureAwait(false);
        }
        if (cfg.TransmitPower is sbyte power)
        {
            await SendConfigAsync(AntMessageId.ChannelTransmitPower,
                OutboundMessages.SetChannelTxPower(channelNumber, power), ct).ConfigureAwait(false);
        }

        var channel = new AntChannel(this, channelNumber, cfg);
        _channels[channelNumber] = channel;
        return channel;
    }

    /// <summary>Start a continuous-scan session on channel 0. Requires the radio idle (no open channel, channel 0 free).</summary>
    public async Task<ScanSession> StartScanAsync(ScanConfiguration cfg, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (cfg is null) throw new ArgumentNullException(nameof(cfg));
        lock (_pendingLock)
        {
            if (_mode == RadioMode.Scanning)
                throw new RadioBusyException("A scan session is already active.");
            if (_openChannelCount > 0)
                throw new RadioBusyException("Cannot start a scan while a channel is open.");
        }
        if (_channels.Length > 0 && _channels[0] is not null)
            throw new RadioBusyException("Channel 0 is configured; unassign it before scanning.");

        await SendConfigAsync(AntMessageId.AssignChannel,
            OutboundMessages.AssignChannel(0, ChannelType.BidirectionalSlave, cfg.NetworkNumber), ct).ConfigureAwait(false);
        await SendConfigAsync(AntMessageId.ChannelId,
            OutboundMessages.SetChannelId(0, cfg.ChannelId), ct).ConfigureAwait(false);
        await SendConfigAsync(AntMessageId.RfFrequency,
            OutboundMessages.SetRfFrequency(0, cfg.RfFrequency), ct).ConfigureAwait(false);
        if (cfg.UseExtendedMessages)
        {
            await SendConfigAsync(AntMessageId.LibConfig,
                OutboundMessages.LibConfig((byte)(AntConstants.LibConfigChannelId | AntConstants.LibConfigRssi)), ct).ConfigureAwait(false);
            await SendConfigAsync(AntMessageId.EnableExtRxMessages,
                OutboundMessages.EnableExtRxMessages(true), ct).ConfigureAwait(false);
        }
        await SendConfigAsync(AntMessageId.OpenRxScanMode, OutboundMessages.OpenRxScanMode(), ct).ConfigureAwait(false);

        var session = new ScanSession(this);
        lock (_pendingLock)
        {
            _scan = session;
            _mode = RadioMode.Scanning;
        }
        return session;
    }

    // ----- internals used by AntChannel / ScanSession -----

    internal ILogger Logger => _logger;

    internal void EnsureChannelOpenAllowed()
    {
        lock (_pendingLock)
        {
            if (_mode == RadioMode.Scanning)
                throw new RadioBusyException("Cannot open a channel while a scan session is active.");
        }
    }

    internal void RegisterChannelOpen()
    {
        lock (_pendingLock)
        {
            _openChannelCount++;
            if (_mode == RadioMode.Idle)
                _mode = RadioMode.Channels;
        }
    }

    internal void UnregisterChannelOpen()
    {
        lock (_pendingLock)
        {
            if (_openChannelCount > 0)
                _openChannelCount--;
            if (_openChannelCount == 0 && _mode == RadioMode.Channels)
                _mode = RadioMode.Idle;
        }
    }

    internal void RemoveChannel(byte channelNumber)
    {
        if (channelNumber < _channels.Length)
            _channels[channelNumber] = null;
    }

    internal void EndScan()
    {
        lock (_pendingLock)
        {
            _scan = null;
            _mode = RadioMode.Idle;
        }
    }

    internal async Task WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _transport.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal async Task<ChannelResponse> SendConfigAsync(AntMessageId id, byte[] frame, CancellationToken ct)
    {
        var msg = await SendCommandAsync(frame,
            m => m.Id == AntMessageId.ChannelResponseEvent && IsResponseTo(m, id),
            id, CommandTimeout, ct).ConfigureAwait(false);
        var resp = InboundMessages.ParseChannelResponse(msg);
        if (resp.Code != ChannelResponseCode.ResponseNoError)
            throw new AntCommandException(id, resp.Code);
        return resp;
    }

    private static bool IsResponseTo(AntMessage m, AntMessageId id)
    {
        var p = m.Payload.Span;
        return p.Length >= 3 && p[1] == (byte)id && p[1] != AntConstants.EventResponseMarker;
    }

    internal async Task<AntMessage> SendCommandAsync(byte[] frame, Func<AntMessage, bool> matcher, AntMessageId command, TimeSpan timeout, CancellationToken ct)
    {
        var pending = new Pending(matcher);
        lock (_pendingLock)
            _pending.Add(pending);
        try
        {
            await WriteFrameAsync(frame, ct).ConfigureAwait(false);
            var delay = Task.Delay(timeout, ct);
            var winner = await Task.WhenAny(pending.Tcs.Task, delay).ConfigureAwait(false);
            if (winner != pending.Tcs.Task)
            {
                ct.ThrowIfCancellationRequested();
                throw new AntTimeoutException(command, timeout);
            }
            return await pending.Tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingLock)
                _pending.Remove(pending);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(512);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await _transport.ReadAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (read <= 0)
                    break; // transport closed

                _parser.Append(buffer.AsSpan(0, read));
                while (_parser.TryReadMessage(out var msg))
                    Dispatch(msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ANT read loop terminated with error.");
            FaultPending(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Dispatch(AntMessage msg)
    {
        lock (_pendingLock)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Matcher(msg))
                {
                    var p = _pending[i];
                    _pending.RemoveAt(i);
                    p.Tcs.TrySetResult(msg);
                    return;
                }
            }
        }

        switch (msg.Id)
        {
            case AntMessageId.ChannelResponseEvent:
            {
                var r = InboundMessages.ParseChannelResponse(msg);
                if (r.IsEvent)
                    RouteEvent(r.Channel, r.Code);
                break;
            }
            case AntMessageId.BroadcastData:
            case AntMessageId.AcknowledgedData:
            case AntMessageId.BurstData:
            {
                var d = InboundMessages.ParseReceivedData(msg);
                RouteData(d);
                break;
            }
            default:
                break;
        }
    }

    private void RouteEvent(byte channel, ChannelResponseCode code)
    {
        ScanSession? scan;
        AntChannel? ch;
        lock (_pendingLock)
        {
            scan = _scan;
            ch = channel < _channels.Length ? _channels[channel] : null;
        }
        if (scan is not null && channel == 0)
        {
            scan.HandleEvent(code);
            return;
        }
        ch?.HandleEvent(code);
    }

    private void RouteData(ReceivedData data)
    {
        ScanSession? scan;
        AntChannel? ch;
        lock (_pendingLock)
        {
            scan = _scan;
            ch = data.Channel < _channels.Length ? _channels[data.Channel] : null;
        }
        if (scan is not null && data.Channel == 0)
        {
            scan.HandleData(data);
            return;
        }
        ch?.HandleData(data);
    }

    private void FaultPending(Exception ex)
    {
        lock (_pendingLock)
        {
            foreach (var p in _pending)
                p.Tcs.TrySetException(new AntException("Transport read loop failed.", ex));
            _pending.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AntDevice));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_readCts is not null)
            _readCts.Cancel();

        try
        {
            await _transport.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        FaultPending(new ObjectDisposedException(nameof(AntDevice)));
        _readCts?.Dispose();
        _writeLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Pending
    {
        public Pending(Func<AntMessage, bool> matcher)
        {
            Matcher = matcher;
        }

        public Func<AntMessage, bool> Matcher { get; }
        public TaskCompletionSource<AntMessage> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
