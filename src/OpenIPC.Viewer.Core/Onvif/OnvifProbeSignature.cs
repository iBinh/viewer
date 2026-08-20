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
    public static bool LooksLikeOnvif(int statusCode, string? body)
    {
        // A 401 on this exact path means something is guarding an ONVIF service.
        // GetSystemDateAndTime is specified as unauthenticated, but a few
        // firmwares put the whole endpoint behind auth anyway — and an unrelated
        // web server 404s a path it doesn't know rather than challenging for it.
        if (statusCode == 401) return true;

        if (string.IsNullOrEmpty(body)) return false;

        // The onvif.org namespace is the decisive marker, and it survives the
        // SOAP Faults some devices answer with (wrong action, missing auth) as
        // well as the success case. A device's own web page never carries it.
        return body!.IndexOf("onvif.org", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
