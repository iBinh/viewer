using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenIPC.Viewer.Core.Discovery;

// Turns a raw routing table into the short list of subnets worth sweeping.
//
// Pure, so the decision that matters — which of a machine's dozens of routes
// look like a camera LAN — is testable without touching iphlpapi or /proc.
public static class ScanTargetPlanner
{
    // /22 is 1022 hosts; anything wider is a backbone or a tunnel's own carrier
    // network (a mesh VPN hands out a /16), never a subnet worth walking host by
    // host. /30 is the narrowest that still has two usable addresses.
    public const int MinPrefixLength = 22;
    public const int MaxPrefixLength = 30;

    /// <param name="routes">Everything the OS reports, unfiltered.</param>
    /// <param name="localAddresses">This machine's own IPv4s, used only to tell
    /// a local subnet from one reached through a router.</param>
    public static IReadOnlyList<ScanTarget> Plan(
        IEnumerable<RouteEntry> routes,
        IEnumerable<string> localAddresses)
    {
        var locals = localAddresses.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<ScanTarget>();

        foreach (var route in routes)
        {
            if (route.PrefixLength < MinPrefixLength || route.PrefixLength > MaxPrefixLength)
                continue;
            // Public destinations are somebody else's network. The private
            // ranges are where LAN cameras live, and the only ones we have any
            // business knocking on.
            if (!IsPrivateIpv4(route.Destination))
                continue;

            var cidr = $"{route.Destination}/{route.PrefixLength.ToString(CultureInfo.InvariantCulture)}";
            // The same prefix often appears on several interfaces or metrics;
            // one entry per subnet is what the user should see.
            if (!seen.Add(cidr))
                continue;
            if (!IpRange.TryParse(cidr, out var range, out _))
                continue;

            targets.Add(new ScanTarget(
                range,
                cidr,
                route.InterfaceName,
                locals.Any(range.Contains) ? ScanTargetOrigin.LocalSubnet : ScanTargetOrigin.RoutedSubnet));
        }

        // Local subnets first: they are the common case and the least surprising
        // thing to be sweeping.
        return targets
            .OrderBy(t => t.Origin == ScanTargetOrigin.LocalSubnet ? 0 : 1)
            .ThenBy(t => t.Cidr, StringComparer.Ordinal)
            .ToList();
    }

    // RFC 1918: 10/8, 172.16/12, 192.168/16. Deliberately not including CGNAT
    // (100.64/10) — that is the carrier network a mesh VPN assigns itself, not a
    // subnet with cameras on it, and it comes as a /16 anyway.
    public static bool IsPrivateIpv4(string address)
    {
        var parts = address.Split('.');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var second)) return false;

        return first switch
        {
            10 => true,
            172 => second >= 16 && second <= 31,
            192 => second == 168,
            _ => false,
        };
    }
}
