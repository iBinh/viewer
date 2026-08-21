using System;

namespace OpenIPC.Viewer.Core.Discovery;

// Knobs for a discovery run. DeepScan gates the active subnet sweep (Slice C),
// which is opt-in because it looks like a port scan on the network.
public sealed record DiscoveryOptions(
    TimeSpan Timeout,
    bool DeepScan = false,
    // Hand-typed sweep range. Null -> the sweep derives the local /24 itself.
    // Set -> it walks exactly these addresses, and runs even with DeepScan off:
    // typing a range is a more explicit opt-in than ticking the box.
    IpRange? Range = null);
