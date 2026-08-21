using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Onvif.Discovery;

namespace OpenIPC.Viewer.Devices.Discovery;

// Works out which subnets to offer the user, so a camera on another VLAN or
// behind a VPN stops being something they have to type an address range for.
//
// The routing table is the only source that finds those: a mesh/VPN interface
// carries its own address on some unrelated carrier network (a /16 of CGNAT,
// say) while the subnet with the cameras on it exists purely as a route through
// that interface. Enumerating interface addresses would miss it entirely.
//
// Where no reader exists (macOS, mobile) this degrades to a /24 per local
// interface — the same guess the sweep used to make on its own, just for every
// candidate instead of one.
public sealed class ScanTargetProvider : IScanTargetProvider
{
    private readonly Routes.IRouteTableReader? _routes;
    private readonly INetworkInterfaceProvider _nics;
    private readonly ILogger<ScanTargetProvider> _logger;

    public ScanTargetProvider(
        Routes.IRouteTableReader? routes,
        INetworkInterfaceProvider nics,
        ILogger<ScanTargetProvider> logger)
    {
        _routes = routes;
        _nics = nics;
        _logger = logger;
    }

    public IReadOnlyList<ScanTarget> GetTargets()
    {
        var localAddresses = SafeCandidates().Select(c => c.Address).ToList();

        var fromRoutes = _routes is null
            ? Array.Empty<ScanTarget>()
            : ScanTargetPlanner.Plan(SafeRoutes(), localAddresses).ToArray();

        // Everything the routes already cover is left alone; the fallback only
        // fills gaps, so a machine with a working reader sees no duplicates.
        var covered = new HashSet<string>(fromRoutes.Select(t => t.Cidr), StringComparer.Ordinal);
        var fallback = LocalSlash24s(localAddresses).Where(t => covered.Add(t.Cidr));

        var targets = fromRoutes.Concat(fallback)
            .OrderBy(t => t.Origin == ScanTargetOrigin.LocalSubnet ? 0 : 1)
            .ThenBy(t => t.Cidr, StringComparer.Ordinal)
            .ToList();

        _logger.LogDebug("Scan targets: {Targets}", string.Join(", ", targets.Select(t => t.Cidr)));
        return targets;
    }

    private IEnumerable<RouteEntry> SafeRoutes()
    {
        try
        {
            return _routes!.Read();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Route table reader threw");
            return Array.Empty<RouteEntry>();
        }
    }

    private IReadOnlyList<NetworkInterfaceInfo> SafeCandidates()
    {
        try
        {
            return _nics.GetCandidates();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Enumerating network interfaces failed");
            return Array.Empty<NetworkInterfaceInfo>();
        }
    }

    // A plain /24 around each local address. No IPv4Mask / GatewayAddresses:
    // both throw on Android's BCL, which is why the sweep never used them either.
    private IEnumerable<ScanTarget> LocalSlash24s(IEnumerable<string> addresses)
    {
        foreach (var address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address)) continue;

            var parts = address.Split('.');
            if (parts.Length != 4) continue;

            var cidr = string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.0/24", parts[0], parts[1], parts[2]);
            if (!ScanTargetPlanner.IsPrivateIpv4(address)) continue;
            if (!IpRange.TryParse(cidr, out var range, out _)) continue;

            yield return new ScanTarget(range, cidr, address, ScanTargetOrigin.LocalSubnet);
        }
    }
}
