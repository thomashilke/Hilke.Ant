using System.Threading.Channels;
using Hilke.Ant.Model;
using Hilke.Ant.Protocol;
using Hilke.Ant.Protocol.Messages;

namespace Hilke.Ant;

/// <summary>
/// A continuous-scan (Open Rx Scan Mode) session. Monopolizes the radio on channel 0 and
/// surfaces every extended broadcast, each carrying a distinguishing <see cref="ChannelId"/>.
/// </summary>
public sealed class ScanSession : IAsyncDisposable
{
    private readonly AntDevice _device;
    private readonly Channel<ScanDataMessage> _rx = System.Threading.Channels.Channel.CreateUnbounded<ScanDataMessage>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _closeCompletion;
    private bool _stopped;
    private bool _disposed;

    internal ScanSession(AntDevice device)
    {
        _device = device;
    }

    /// <summary>Every extended broadcast on channel 0 during the scan.</summary>
    public IAsyncEnumerable<ScanDataMessage> ReceiveAsync(CancellationToken ct = default)
        => _rx.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Advanced: send an acknowledged reply on channel 0 to a device whose id was set beforehand.
    /// The caller must have set channel 0's id (via a prior channel-id command) to the target.
    /// </summary>
    public Task SendAcknowledgedAsync(ChannelId target, ReadOnlyMemory<byte> page8, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        // Address the target, then send. In scan mode the radio replies on channel 0.
        var idFrame = OutboundMessages.SetChannelId(0, target);
        var dataFrame = OutboundMessages.AcknowledgedData(0, page8.Span);
        return SendSequenceAsync(idFrame, dataFrame, ct);
    }

    private async Task SendSequenceAsync(byte[] first, byte[] second, CancellationToken ct)
    {
        await _device.WriteFrameAsync(first, ct).ConfigureAwait(false);
        await _device.WriteFrameAsync(second, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
            _closeCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await _device.SendConfigAsync(AntMessageId.CloseChannel, OutboundMessages.CloseChannel(0), ct).ConfigureAwait(false);
        var completion = _closeCompletion;
        if (completion is not null)
        {
            using var reg = ct.Register(() => completion.TrySetCanceled(ct));
            await completion.Task.ConfigureAwait(false);
        }
        await _device.SendConfigAsync(AntMessageId.UnassignChannel, OutboundMessages.UnassignChannel(0), ct).ConfigureAwait(false);
        _device.EndScan();
        _rx.Writer.TryComplete();
    }

    // ----- read-loop callbacks -----

    internal void HandleData(ReceivedData data)
    {
        // Scan requires extended messages; the device id comes from the appended channel id.
        ChannelId id = data.ExtendedId ?? new ChannelId(0, 0, 0);
        var msg = new ScanDataMessage(id, data.Kind, data.Payload, data.Rssi, DateTimeOffset.UtcNow);
        _rx.Writer.TryWrite(msg);
    }

    internal void HandleEvent(ChannelResponseCode code)
    {
        if (code == ChannelResponseCode.EventChannelClosed)
        {
            TaskCompletionSource<bool>? tcs;
            lock (_gate) { tcs = _closeCompletion; _closeCompletion = null; }
            tcs?.TrySetResult(true);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ScanSession));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (!_stopped)
                await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // best effort
            _device.EndScan();
        }
        _rx.Writer.TryComplete();
    }
}
