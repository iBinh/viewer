using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenIPC.Viewer.Core.Onvif;

// "Does host:port speak ONVIF?", answered over plain HTTP without credentials.
//
// Separate from OnvifProbeService (the real capabilities/profiles/stream-URI
// chain) on purpose: that one is built on the generated WCF contracts, which
// don't work on every platform and need a login. This is one hand-written SOAP
// POST, so the subnet sweep can flag ONVIF devices it finds by address —
// WS-Discovery only ever reaches the local link, so a camera on a routed subnet
// has no other way to be recognised as ONVIF.
public interface IOnvifFingerprint
{
    /// <summary>
    /// The device-service URI when <paramref name="host"/> answers ONVIF there,
    /// otherwise null. Never throws for an unreachable or unrelated host.
    /// </summary>
    Task<Uri?> ProbeAsync(string host, int port, CancellationToken ct);
}
