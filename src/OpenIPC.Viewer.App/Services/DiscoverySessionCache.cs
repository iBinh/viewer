using System;
using System.Collections.Generic;
using OpenIPC.Viewer.Core.Discovery;

namespace OpenIPC.Viewer.App.Services;

// Session-scoped memory for the discovery dialog, so the user can add several
// cameras from ONE scan: results, the typed credentials and the deep-scan flag
// all survive closing and reopening the dialog. In-memory only on purpose —
// the credentials must never be persisted to disk from here (they end up in
// the secrets store per-camera once a camera is saved).
public sealed class DiscoverySessionCache
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DiscoveredDevice> _devices =
        new(StringComparer.OrdinalIgnoreCase);

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool DeepScan { get; set; }

    // Raw text of the hand-typed sweep range, kept unparsed so reopening the
    // dialog shows exactly what was typed (including a half-finished entry).
    // Empty = sweep the auto-detected local /24.
    public string IpRangeText { get; set; } = "";

    // Explicit tick choices the user made for auto-detected subnets, by CIDR.
    // Only choices that override the per-origin default are meaningful; a CIDR
    // with no entry falls back to that default (local subnets ticked, routed
    // ones — behind a VPN or on another VLAN — not). So a subnet that appears
    // later in the session (a VPN brought up mid-session) still arrives at its
    // safe default rather than inheriting a stale tick.
    public Dictionary<string, bool> TargetSelections { get; } = new(StringComparer.Ordinal);

    // "Use these credentials for all cameras" — on (default) carries the typed
    // login into the next add; off makes every camera start with blank fields
    // (mixed-credential parks).
    public bool ReuseCredentials { get; set; } = true;

    public IReadOnlyList<DiscoveredDevice> Snapshot()
    {
        lock (_sync)
            return new List<DiscoveredDevice>(_devices.Values);
    }

    public void Put(DiscoveredDevice device)
    {
        lock (_sync)
            _devices[device.Host] = device;
    }

    public void Clear()
    {
        lock (_sync)
            _devices.Clear();
    }
}
