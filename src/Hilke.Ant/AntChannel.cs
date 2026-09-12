using System.Threading.Channels;
using Hilke.Ant.Model;
using Hilke.Ant.Protocol;
using Hilke.Ant.Protocol.Messages;

namespace Hilke.Ant;

/// <summary>
/// A single ANT channel: maps RF events + inactivity onto the library lifecycle
/// <see cref="ChannelState"/> and exposes RX as an async stream plus master TX.
/// </summary>
internal sealed class AntChannel : IAsyncDisposable
{
    private readonly AntDevice _device;
    private readonly Channel<AntDataMessage> _rx = System.Threading.Channels.Channel.CreateUnbounded<AntDataMessage>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private readonly object _gate = new();
    private readonly Timer? _inactivityTimer;
    private TaskCompletionSource<bool>? _txCompletion;
    private TaskCompletionSource<bool>? _closeCompletion;
    private bool _disposed;

    internal AntChannel(AntDevice device, byte channelNumber, ChannelConfiguration configuration)
    {
        _device = device;
        ChannelNumber = channelNumber;
        Configuration = configuration;
        State = ChannelState.Configured;
        if (configuration.InactivityTimeout is not null)
            _inactivityTimer = new Timer(OnInactivityElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public byte ChannelNumber { get; }
    public ChannelConfiguration Configuration { get; }
    public ChannelState State { get; private set; }
    public ChannelId? TrackedDevice { get; private set; }

    /// <summary>Raised on every library-level state transition.</summary>
    public event EventHandler<ChannelStateChangedEventArgs>? StateChanged;

    /// <summary>Raised on <c>EventTx</c> so the consumer may load the next broadcast page.</summary>
    public event EventHandler? TransmitReady;

    /// <summary>The primary received-data stream for this channel.</summary>
    public IAsyncEnumerable<AntDataMessage> ReceiveAsync(CancellationToken ct = default)
        => _rx.Reader.ReadAllAsync(ct);

    // ----- lifecycle -----

    public async Task OpenAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _device.EnsureChannelOpenAllowed();
        RequireState(ChannelState.Configured, nameof(OpenAsync));
        await _device.SendConfigAsync(AntMessageId.OpenChannel, OutboundMessages.OpenChannel(ChannelNumber), ct).ConfigureAwait(false);
        _device.RegisterChannelOpen();
        Transition(ChannelState.Searching, ChannelTransitionReason.Requested);
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (State is ChannelState.Unconfigured or ChannelState.Configured)
                throw new InvalidChannelStateException(State, nameof(CloseAsync));
            _closeCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetStateLocked(ChannelState.Closing, ChannelTransitionReason.Requested);
        }
        RaisePendingTransitions();
        await _device.SendConfigAsync(AntMessageId.CloseChannel, OutboundMessages.CloseChannel(ChannelNumber), ct).ConfigureAwait(false);
        // Wait for the device to confirm closure with EventChannelClosed.
        var completion = _closeCompletion;
        if (completion is not null)
        {
            using var reg = ct.Register(() => completion.TrySetCanceled(ct));
            await completion.Task.ConfigureAwait(false);
        }
    }

    public async Task UnassignAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        RequireState(ChannelState.Configured, nameof(UnassignAsync));
        await _device.SendConfigAsync(AntMessageId.UnassignChannel, OutboundMessages.UnassignChannel(ChannelNumber), ct).ConfigureAwait(false);
        Transition(ChannelState.Unconfigured, ChannelTransitionReason.Requested);
        _device.RemoveChannel(ChannelNumber);
        _rx.Writer.TryComplete();
    }

    // ----- master TX -----

    public Task SetBroadcastDataAsync(ReadOnlyMemory<byte> page8, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureMaster(nameof(SetBroadcastDataAsync));
        return _device.WriteFrameAsync(OutboundMessages.BroadcastData(ChannelNumber, page8.Span), ct);
    }

    public async Task SendAcknowledgedAsync(ReadOnlyMemory<byte> page8, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureMaster(nameof(SendAcknowledgedAsync));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_txCompletion is not null)
                throw new InvalidOperationException("A transfer is already in progress on this channel.");
            _txCompletion = tcs;
        }
        try
        {
            await _device.WriteFrameAsync(OutboundMessages.AcknowledgedData(ChannelNumber, page8.Span), ct).ConfigureAwait(false);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_txCompletion, tcs)) _txCompletion = null; }
        }
    }

    public async Task SendBurstAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureMaster(nameof(SendBurstAsync));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_txCompletion is not null)
                throw new InvalidOperationException("A transfer is already in progress on this channel.");
            _txCompletion = tcs;
        }
        try
        {
            int packets = (data.Length + 7) / 8;
            if (packets == 0) packets = 1;
            byte[] page = new byte[8];
            for (int i = 0; i < packets; i++)
            {
                Array.Clear(page);
                int offset = i * 8;
                int take = Math.Min(8, data.Length - offset);
                if (take > 0)
                    data.Span.Slice(offset, take).CopyTo(page);
                // 3-bit rolling sequence in high nibble; final packet sets bit 2 per ANT burst spec.
                int seq = i & 0x03;
                bool last = i == packets - 1;
                byte seqChannel = (byte)(((seq | (last ? 0x04 : 0x00)) << 5) | (ChannelNumber & 0x1F));
                await _device.WriteFrameAsync(OutboundMessages.BurstData(seqChannel, page), ct).ConfigureAwait(false);
            }
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_txCompletion, tcs)) _txCompletion = null; }
        }
    }

    // ----- read-loop callbacks -----

    internal void HandleData(ReceivedData data)
    {
        ChannelId? id = data.ExtendedId;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (State is ChannelState.Searching or ChannelState.Lost)
            {
                if (id is ChannelId cid)
                    TrackedDevice = cid;
                SetStateLocked(ChannelState.Tracking, ChannelTransitionReason.DataReceived);
            }
            ArmInactivityTimer();
        }
        RaisePendingTransitions();

        var msg = new AntDataMessage(id ?? TrackedDevice, data.Kind, data.Payload, data.Rssi, now);
        _rx.Writer.TryWrite(msg);
    }

    internal void HandleEvent(ChannelResponseCode code)
    {
        switch (code)
        {
            case ChannelResponseCode.EventTx:
                TransmitReady?.Invoke(this, EventArgs.Empty);
                break;
            case ChannelResponseCode.EventTransferTxCompleted:
                CompleteTx(null);
                break;
            case ChannelResponseCode.EventTransferTxFailed:
                CompleteTx(new AntCommandException(AntMessageId.AcknowledgedData, ChannelResponseCode.EventTransferTxFailed));
                break;
            case ChannelResponseCode.EventRxFailGoToSearch:
                lock (_gate)
                {
                    if (State == ChannelState.Tracking)
                        SetStateLocked(ChannelState.Lost, ChannelTransitionReason.DeviceLost);
                }
                RaisePendingTransitions();
                break;
            case ChannelResponseCode.EventRxSearchTimeout:
                // Release the open-channel mutual exclusion before publishing the state
                // transition: a consumer reacting to StateChanged (e.g. re-scanning once the
                // channel frees up) must never observe a stale "channel is open" rejection.
                _device.UnregisterChannelOpen();
                Transition(ChannelState.Configured, ChannelTransitionReason.SearchTimedOut);
                break;
            case ChannelResponseCode.EventChannelClosed:
                _device.UnregisterChannelOpen();
                Transition(ChannelState.Configured, ChannelTransitionReason.Requested);
                CompleteClose();
                break;
            default:
                break;
        }
    }

    // ----- helpers -----

    private void CompleteTx(Exception? error)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_gate) { tcs = _txCompletion; }
        if (tcs is null) return;
        if (error is null) tcs.TrySetResult(true);
        else tcs.TrySetException(error);
    }

    private void CompleteClose()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_gate) { tcs = _closeCompletion; _closeCompletion = null; }
        tcs?.TrySetResult(true);
    }

    private void OnInactivityElapsed(object? _)
    {
        bool changed = false;
        lock (_gate)
        {
            if (State == ChannelState.Tracking)
            {
                SetStateLocked(ChannelState.Lost, ChannelTransitionReason.InactivityTimeout);
                changed = true;
            }
        }
        if (changed)
            RaisePendingTransitions();
    }

    private void ArmInactivityTimer()
    {
        if (_inactivityTimer is not null && Configuration.InactivityTimeout is TimeSpan t)
            _inactivityTimer.Change(t, Timeout.InfiniteTimeSpan);
    }

    private void RequireState(ChannelState required, string operation)
    {
        lock (_gate)
        {
            if (State != required)
                throw new InvalidChannelStateException(State, operation);
        }
    }

    private void EnsureMaster(string operation)
    {
        if (Configuration.Type == ChannelType.SlaveReceiveOnly)
            throw new InvalidOperationException($"'{operation}' is not valid on a receive-only slave channel.");
    }

    /// <summary>Transition outside the gate (acquires it), raising StateChanged after release.</summary>
    private void Transition(ChannelState next, ChannelTransitionReason reason)
    {
        lock (_gate)
            SetStateLocked(next, reason);
        RaisePendingTransitions();
    }

    // Pending transition tuple captured under the gate, raised outside it to avoid re-entrancy.
    private (ChannelState oldState, ChannelState newState, ChannelTransitionReason reason)? _pendingTransition;

    private ChannelState SetStateLocked(ChannelState next, ChannelTransitionReason reason)
    {
        if (State == next)
            return State;
        var old = State;
        State = next;
        _pendingTransition = (old, next, reason);
        return next;
    }

    private void RaisePendingTransitions()
    {
        (ChannelState oldState, ChannelState newState, ChannelTransitionReason reason)? t;
        lock (_gate) { t = _pendingTransition; _pendingTransition = null; }
        if (t is { } tr)
            StateChanged?.Invoke(this, new ChannelStateChangedEventArgs(tr.oldState, tr.newState, tr.reason));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AntChannel));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            lock (_gate)
            {
                if (State is ChannelState.Searching or ChannelState.Tracking or ChannelState.Lost)
                {
                    // best-effort close
                }
            }
            if (State is ChannelState.Searching or ChannelState.Tracking or ChannelState.Lost)
            {
                await _device.WriteFrameAsync(OutboundMessages.CloseChannel(ChannelNumber), CancellationToken.None).ConfigureAwait(false);
                _device.UnregisterChannelOpen();
            }
        }
        catch
        {
            // best effort
        }

        if (_inactivityTimer is not null)
            await _inactivityTimer.DisposeAsync().ConfigureAwait(false);
        _rx.Writer.TryComplete();
    }
}
