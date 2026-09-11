using Hilke.Ant;
using Hilke.Ant.Model;

namespace Hilke.Ant.Plus;

/// <summary>Lifecycle state of a <see cref="PowerMeterCalibrationSession"/> request.</summary>
public enum PowerMeterCalibrationState
{
    Idle,
    SendingRequest,
    AwaitingResponse,
    Succeeded,
    Failed,
    TimedOut,
}

/// <summary>Terminal outcome of a calibration request.</summary>
public enum CalibrationOutcome
{
    Success,
    Failed,
    TimedOut,
    TransmitFailed,
}

/// <summary>
/// A decoded ANT+ calibration response page (0x01) from a power sensor. <see cref="CalibrationData"/>
/// is the signed 16-bit zero offset (bytes 6 LSB / 7 MSB).
/// </summary>
public readonly record struct CalibrationResponse(
    byte CalibrationId,
    byte AutoZeroStatus,
    short CalibrationData,
    bool Success,
    byte PageNumber,
    DateTimeOffset At) : IAntPlusDataPage;

/// <summary>Typed result of a completed calibration request.</summary>
public readonly record struct PowerMeterCalibrationResult(
    CalibrationOutcome Outcome,
    byte CalibrationId,
    short ZeroOffset,
    byte AutoZeroStatus,
    DateTimeOffset At)
{
    /// <summary>True when the sensor reported a successful calibration.</summary>
    public bool Success => Outcome == CalibrationOutcome.Success;
}

/// <summary>
/// Decodes the ANT+ common calibration page (0x01), surfacing only sensor-originated
/// response ids (success 0xAC / fail 0xAF); request ids and other pages are rejected.
/// </summary>
public sealed class CalibrationDecoder : IDataPageDecoder<CalibrationResponse>
{
    /// <summary>Common calibration data page number.</summary>
    public const byte Page = 0x01;

    /// <summary>Manual-zero calibration request id (display to sensor).</summary>
    public const byte ManualZeroRequestId = 0xAA;

    /// <summary>Auto-zero configuration request id (display to sensor).</summary>
    public const byte AutoZeroConfigRequestId = 0xAB;

    /// <summary>Calibration-success response id (sensor to display).</summary>
    public const byte SuccessResponseId = 0xAC;

    /// <summary>Calibration-fail response id (sensor to display).</summary>
    public const byte FailResponseId = 0xAF;

    public bool TryDecode(ReadOnlySpan<byte> payload8, out CalibrationResponse reading)
    {
        reading = default;
        if (payload8.Length < 8)
            return false;
        if ((payload8[0] & 0x7F) != Page)
            return false;

        byte id = payload8[1];
        if (id != SuccessResponseId && id != FailResponseId)
            return false;

        byte autoZero = payload8[2];
        short data = (short)(payload8[6] | (payload8[7] << 8));
        reading = new CalibrationResponse(id, autoZero, data, id == SuccessResponseId, Page, DateTimeOffset.UtcNow);
        return true;
    }
}

/// <summary>
/// Drives an ANT+ power-meter calibration handshake: sends a calibration request as acknowledged
/// data, then resolves to a typed result when the sensor's response page is pushed in via
/// <see cref="HandleData"/>. The session never consumes the channel's single-consumer receive
/// stream itself; the owning read pump feeds responses.
/// </summary>
public sealed class PowerMeterCalibrationSession
{
    private readonly AntChannel _channel;
    private readonly CalibrationDecoder _decoder = new();
    private readonly object _gate = new();
    private PowerMeterCalibrationState _state = PowerMeterCalibrationState.Idle;
    private TaskCompletionSource<PowerMeterCalibrationResult>? _pending;
    private CancellationTokenSource? _timeoutCts;
    private PowerMeterCalibrationState? _pendingRaise;

    internal PowerMeterCalibrationSession(AntChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    /// <summary>Raised on every calibration state transition.</summary>
    public event EventHandler<PowerMeterCalibrationState>? StateChanged;

    /// <summary>Current calibration state.</summary>
    public PowerMeterCalibrationState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Manual-zero calibration request payload (display to sensor, acknowledged).</summary>
    public static byte[] BuildManualZeroRequest() =>
        new byte[] { CalibrationDecoder.Page, CalibrationDecoder.ManualZeroRequestId, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>Auto-zero configuration request payload; byte 2 enables/disables auto-zero.</summary>
    public static byte[] BuildAutoZeroConfigRequest(bool enable) =>
        new byte[] { CalibrationDecoder.Page, CalibrationDecoder.AutoZeroConfigRequestId, (byte)(enable ? 0x01 : 0x00), 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>Request a manual-zero calibration and await the sensor's response.</summary>
    public Task<PowerMeterCalibrationResult> RequestManualZeroAsync(TimeSpan timeout, CancellationToken ct = default) =>
        RunAsync(BuildManualZeroRequest(), timeout, ct);

    /// <summary>Configure auto-zero on the sensor and await its response.</summary>
    public Task<PowerMeterCalibrationResult> ConfigureAutoZeroAsync(bool enable, TimeSpan timeout, CancellationToken ct = default) =>
        RunAsync(BuildAutoZeroConfigRequest(enable), timeout, ct);

    private async Task<PowerMeterCalibrationResult> RunAsync(byte[] request, TimeSpan timeout, CancellationToken ct)
    {
        TaskCompletionSource<PowerMeterCalibrationResult> tcs;
        lock (_gate)
        {
            if (_state is PowerMeterCalibrationState.SendingRequest or PowerMeterCalibrationState.AwaitingResponse)
                throw new InvalidOperationException("A calibration is already in progress.");
            tcs = new TaskCompletionSource<PowerMeterCalibrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            SetStateLocked(PowerMeterCalibrationState.SendingRequest);
        }
        RaiseState();

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        lock (_gate)
            _timeoutCts = linked;
        using var registration = linked.Token.Register(() => OnDeadline(ct));

        try
        {
            await _channel.SendAcknowledgedAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Deadline/cancellation is handled by OnDeadline.
        }
        catch (AntCommandException)
        {
            Complete(
                new PowerMeterCalibrationResult(CalibrationOutcome.TransmitFailed, request[1], 0, 0, DateTimeOffset.UtcNow),
                PowerMeterCalibrationState.Failed);
        }

        lock (_gate)
        {
            if (_state == PowerMeterCalibrationState.SendingRequest)
                SetStateLocked(PowerMeterCalibrationState.AwaitingResponse);
        }
        RaiseState();

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            linked.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_timeoutCts, linked))
                    _timeoutCts = null;
            }
        }
    }

    /// <summary>Feed a received payload into the session; resolves the pending request on a response.</summary>
    public void HandleData(ReadOnlySpan<byte> payload8, DateTimeOffset at)
    {
        TaskCompletionSource<PowerMeterCalibrationResult>? tcs = null;
        PowerMeterCalibrationResult result = default;
        lock (_gate)
        {
            if (_state is not (PowerMeterCalibrationState.SendingRequest or PowerMeterCalibrationState.AwaitingResponse))
                return;
            if (!_decoder.TryDecode(payload8, out var resp))
                return;
            result = new PowerMeterCalibrationResult(
                resp.Success ? CalibrationOutcome.Success : CalibrationOutcome.Failed,
                resp.CalibrationId,
                resp.CalibrationData,
                resp.AutoZeroStatus,
                at);
            SetStateLocked(resp.Success ? PowerMeterCalibrationState.Succeeded : PowerMeterCalibrationState.Failed);
            tcs = _pending;
            _pending = null;
        }
        RaiseState();
        tcs?.TrySetResult(result);
    }

    private void OnDeadline(CancellationToken ct)
    {
        bool canceled = ct.IsCancellationRequested;
        TaskCompletionSource<PowerMeterCalibrationResult>? tcs;
        lock (_gate)
        {
            if (_state is not (PowerMeterCalibrationState.SendingRequest or PowerMeterCalibrationState.AwaitingResponse))
                return;
            tcs = _pending;
            _pending = null;
            SetStateLocked(PowerMeterCalibrationState.TimedOut);
        }
        RaiseState();
        if (canceled)
            tcs?.TrySetCanceled(ct);
        else
            tcs?.TrySetResult(new PowerMeterCalibrationResult(CalibrationOutcome.TimedOut, 0, 0, 0, DateTimeOffset.UtcNow));
    }

    private void Complete(PowerMeterCalibrationResult result, PowerMeterCalibrationState terminal)
    {
        TaskCompletionSource<PowerMeterCalibrationResult>? tcs;
        lock (_gate)
        {
            tcs = _pending;
            if (tcs is null)
                return;
            _pending = null;
            SetStateLocked(terminal);
        }
        RaiseState();
        tcs.TrySetResult(result);
    }

    private void SetStateLocked(PowerMeterCalibrationState next)
    {
        if (_state == next)
            return;
        _state = next;
        _pendingRaise = next;
    }

    private void RaiseState()
    {
        PowerMeterCalibrationState? next;
        lock (_gate) { next = _pendingRaise; _pendingRaise = null; }
        if (next is { } n)
            StateChanged?.Invoke(this, n);
    }
}
