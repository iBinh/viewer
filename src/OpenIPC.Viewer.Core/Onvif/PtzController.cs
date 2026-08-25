using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.Onvif;

// Translates joystick input into throttled ContinuousMove SOAP calls.
// One tick = MoveTick (80ms). Each ContinuousMove sends timeout=2*MoveTick so
// the camera self-stops within ~160ms if our pump dies — safety against a
// runaway pan when network drops mid-drag. StopAsync sends one explicit Stop.
public sealed class PtzController : IAsyncDisposable
{
    private static readonly TimeSpan MoveTick = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromMilliseconds(160);

    // How long a timed continuous move stands in for one step on a camera with
    // no RelativeMove. Long enough to see, short enough that a lost stop costs
    // nothing.
    private static readonly TimeSpan StepFallbackDuration = TimeSpan.FromMilliseconds(350);

    private readonly IOnvifClient _client;
    private readonly OnvifEndpoint _endpoint;
    private readonly string _profileToken;

    private readonly object _gate = new();
    private PtzVelocity _current = PtzVelocity.Zero;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private PtzCapabilities? _capabilities;

    public PtzController(IOnvifClient client, OnvifEndpoint endpoint, string profileToken)
    {
        _client = client;
        _endpoint = endpoint;
        _profileToken = profileToken;
    }

    // Called by the joystick on PointerMoved while captured. The velocity must
    // already be clamped to [-1, 1]. Starts a background pump on the first
    // call; subsequent calls just update the velocity.
    public void SetVelocity(PtzVelocity velocity)
    {
        lock (_gate)
        {
            _current = velocity;
            if (_pumpTask is null)
            {
                _pumpCts = new CancellationTokenSource();
                _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token));
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Task? toAwait;
        CancellationTokenSource? toCancel;
        lock (_gate)
        {
            _current = PtzVelocity.Zero;
            toAwait = _pumpTask;
            toCancel = _pumpCts;
            _pumpTask = null;
            _pumpCts = null;
        }
        toCancel?.Cancel();
        if (toAwait is not null)
        {
            try { await toAwait.ConfigureAwait(false); }
            catch { /* expected on cancel */ }
        }
        toCancel?.Dispose();

        // Explicit stop regardless of whether the pump got a final zero through.
        try { await _client.StopPtzAsync(_endpoint, _profileToken, ct).ConfigureAwait(false); }
        catch { /* camera may already be idle */ }
    }

    // Read once and kept: the answer is a property of the camera, and the UI
    // asks it on every render to decide which buttons exist. A camera that
    // cannot answer gets the continuous-only profile.
    public async Task<PtzCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        if (_capabilities is { } cached) return cached;

        PtzCapabilities caps;
        try
        {
            caps = await _client.GetPtzCapabilitiesAsync(_endpoint, _profileToken, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            caps = PtzCapabilities.ContinuousOnly;
        }

        _capabilities = caps;
        return caps;
    }

    // One nudge, in normalized [-1, 1] per axis.
    //
    // RelativeMove is the right operation for a step: a single request the
    // camera executes and finishes, where a continuous move has to be started
    // and stopped and leaves the camera drifting if the stop is lost. Cameras
    // without it fall back to a short timed continuous move — the timeout is
    // the step length, so the camera stops itself even if the app dies mid-move.
    public async Task StepAsync(PtzVelocity step, float speed, CancellationToken ct)
    {
        var caps = await GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (caps.SupportsRelative)
        {
            var scaled = new PtzVelocity(
                caps.RelativePan.FromNormalized(step.PanX) - Midpoint(caps.RelativePan),
                caps.RelativeTilt.FromNormalized(step.TiltY) - Midpoint(caps.RelativeTilt),
                caps.RelativeZoom.FromNormalized(step.Zoom) - Midpoint(caps.RelativeZoom));
            await _client.RelativeMoveAsync(_endpoint, _profileToken, scaled, speed, ct).ConfigureAwait(false);
            return;
        }

        await _client.ContinuousMoveAsync(
            _endpoint, _profileToken,
            new PtzVelocity(step.PanX * speed, step.TiltY * speed, step.Zoom * speed),
            StepFallbackDuration, ct).ConfigureAwait(false);
    }

    // A translation of zero must stay zero after scaling. The range maps
    // normalized 0 onto its midpoint, which for an asymmetric range is not 0 —
    // and a camera reading that as "move by midpoint" would drift on every step
    // of an axis the user never touched.
    private static float Midpoint(PtzRange range) => range.IsValid ? (range.Max + range.Min) / 2f : 0f;

    public Task GoHomeAsync(float speed, CancellationToken ct) =>
        _client.GotoHomeAsync(_endpoint, _profileToken, speed, ct);

    public Task SetHomeAsync(CancellationToken ct) =>
        _client.SetHomeAsync(_endpoint, _profileToken, ct);

    public Task<PtzStatus> GetStatusAsync(CancellationToken ct) =>
        _client.GetPtzStatusAsync(_endpoint, _profileToken, ct);

    public Task<IReadOnlyList<PtzPreset>> GetPresetsAsync(CancellationToken ct) =>
        _client.GetPresetsAsync(_endpoint, _profileToken, ct);

    public Task GotoPresetAsync(string presetToken, CancellationToken ct) =>
        _client.GotoPresetAsync(_endpoint, _profileToken, presetToken, ct);

    public Task<string> SetPresetAsync(string name, CancellationToken ct) =>
        _client.SetPresetAsync(_endpoint, _profileToken, name, ct);

    public Task RemovePresetAsync(string presetToken, CancellationToken ct) =>
        _client.RemovePresetAsync(_endpoint, _profileToken, presetToken, ct);

    public ValueTask DisposeAsync() =>
        new(StopAsync(CancellationToken.None));

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PtzVelocity v;
            lock (_gate) v = _current;

            if (v.PanX == 0 && v.TiltY == 0 && v.Zoom == 0)
                break;

            try
            {
                await _client.ContinuousMoveAsync(_endpoint, _profileToken, v, MoveTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Network blip mid-drag — swallow, next tick retries.
            }

            try { await Task.Delay(MoveTick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
