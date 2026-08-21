using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.Devices.Onvif;

// One unauthenticated SOAP POST per host — the sweep's ONVIF equivalent of the
// Majestic ping. Raw HttpClient rather than the generated ONVIF contracts, for
// the same reason MajesticHttpClient is raw: the WCF XmlSerializer stack behind
// OnvifProbeService fails to build on Android, and the sweep has to run there.
//
// It fires at every host that answered on an HTTP port, so it will also knock
// on printers and routers. That is one small POST to a path they don't serve,
// and the sweep is already opt-in for exactly this kind of nosiness.
public sealed class OnvifHttpFingerprint : IOnvifFingerprint, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    // A camera answers in a couple of KB. The cap is about the hosts that are
    // NOT cameras: something on port 80 may hand back a huge page, or a stream,
    // and the sweep must not sit there draining it.
    private const int MaxProbeBytes = 32 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger<OnvifHttpFingerprint> _logger;

    public OnvifHttpFingerprint(ILogger<OnvifHttpFingerprint> logger)
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = RequestTimeout,
        };
        _logger = logger;
    }

    public async Task<Uri?> ProbeAsync(string host, int port, CancellationToken ct)
    {
        var uri = new Uri($"http://{host}:{port}{OnvifProbeSignature.DeviceServicePath}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(OnvifProbeSignature.RequestEnvelope, Encoding.UTF8),
            };
            request.Content.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(OnvifProbeSignature.ContentType);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // Read on every status: a device that faults or challenges this call
            // still names the onvif.org namespace in the body if it is really an
            // ONVIF stack, and the status on its own proves nothing.
            var body = await ReadBoundedAsync(response, ct).ConfigureAwait(false);

            if (!OnvifProbeSignature.LooksLikeOnvif(body))
                return null;

            _logger.LogDebug("ONVIF fingerprint hit at {Uri}", uri);
            return uri;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unreachable, not HTTP, timed out, TLS junk on port 80 — all just
            // mean "not an ONVIF device here".
            _logger.LogDebug(ex, "ONVIF fingerprint failed for {Uri}", uri);
            return null;
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxProbeBytes];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, filled, buffer.Length - filled, ct).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, filled);
    }

    public void Dispose() => _http.Dispose();
}
