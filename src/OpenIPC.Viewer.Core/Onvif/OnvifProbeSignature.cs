using System;

namespace OpenIPC.Viewer.Core.Onvif;

// The request/response shape of the cheap "is there an ONVIF service here?"
// check, kept apart from the HTTP client that performs it so the decision is
// testable without a socket — the Majestic fingerprint's LooksLikeMajesticConfig
// plays the same role for that protocol.
public static class OnvifProbeSignature
{
    // GetSystemDateAndTime is *the* unauthenticated ONVIF call: the Core spec
    // requires devices to answer it with no credentials, which is what makes it
    // usable as a fingerprint before the user has typed a login. SOAP 1.2, so it
    // goes out as application/soap+xml.
    public const string ContentType = "application/soap+xml; charset=utf-8";

    public const string RequestEnvelope =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
        "<s:Body><GetSystemDateAndTime xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/></s:Body>" +
        "</s:Envelope>";

    // The conventional path. Devices built on gSOAP answer ONVIF on anything
    // under /onvif/, but this is the one every stack agrees on.
    public const string DeviceServicePath = "/onvif/device_service";

    /// <summary>
    /// Whether a response to <see cref="RequestEnvelope"/> came from an ONVIF service.
    /// </summary>
    /// <remarks>
    /// Content is the only accepted evidence, whatever the status code. An
    /// earlier version also took a bare 401 as proof, on the theory that an
    /// unrelated server would 404 a path it doesn't serve — but plenty of
    /// embedded web servers (routers, NAS boxes) put their whole web root behind
    /// HTTP Basic auth and challenge *every* path, which made each one look like
    /// a high-confidence ONVIF camera. Missing the rare firmware that guards
    /// GetSystemDateAndTime — which the ONVIF Core spec requires to be callable
    /// without credentials — is the cheaper mistake: such a device still shows up
    /// from its open RTSP/HTTP ports and can still be added by hand.
    /// </remarks>
    public static bool LooksLikeOnvif(string? body)
    {
        if (string.IsNullOrEmpty(body)) return false;

        // The onvif.org namespace is the decisive marker, and it survives the
        // SOAP Faults some devices answer with (wrong action, missing auth) as
        // well as the success case. A device's own web page never carries it.
        return body!.IndexOf("onvif.org", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
