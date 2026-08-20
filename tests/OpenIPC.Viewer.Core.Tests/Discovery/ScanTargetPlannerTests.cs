using System.Linq;
using OpenIPC.Viewer.Core.Discovery;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Discovery;

// Picking sweepable subnets out of a raw routing table. This is the decision
// that makes discovery need no typing, and the one that decides which networks
// the app knocks on unprompted — so it is pinned down rather than trusted.
public sealed class ScanTargetPlannerTests
{
    // The real table from a machine with a LAN adapter and a NetBird/WireGuard
    // mesh, trimmed to the rows that matter. The cameras live on 192.168.3.0/24,
    // reachable only as a route through the tunnel.
    private static RouteEntry[] RealWorldTable() =>
    [
        new("0.0.0.0", 0, "Ethernet 2"),            // default route
        new("192.168.1.0", 24, "Ethernet 2"),       // the LAN this machine is on
        new("192.168.1.160", 32, "Ethernet 2"),     // own address
        new("192.168.1.255", 32, "Ethernet 2"),     // broadcast
        new("192.168.3.0", 24, "wt0"),              // the camera subnet, via tunnel
        new("192.168.3.255", 32, "wt0"),
        new("100.85.0.0", 16, "wt0"),               // the tunnel's own carrier net
        new("127.0.0.0", 8, "Loopback"),
        new("224.0.0.0", 4, "Ethernet 2"),          // multicast
    ];

    [Fact]
    public void RealWorldTable_YieldsExactlyTheTwoUsefulSubnets()
    {
        var targets = ScanTargetPlanner.Plan(RealWorldTable(), ["192.168.1.160", "100.85.8.1"]);

        Assert.Equal(["192.168.1.0/24", "192.168.3.0/24"], targets.Select(t => t.Cidr).ToArray());
    }

    [Fact]
    public void SubnetReachedThroughATunnel_IsMarkedRouted()
    {
        var targets = ScanTargetPlanner.Plan(RealWorldTable(), ["192.168.1.160", "100.85.8.1"]);

        // This distinction is the whole point: a routed subnet is one that
        // multicast WS-Discovery/mDNS can never reach, so the sweep is the only
        // way to find anything on it.
        var local = targets.Single(t => t.Cidr == "192.168.1.0/24");
        var routed = targets.Single(t => t.Cidr == "192.168.3.0/24");

        Assert.Equal(ScanTargetOrigin.LocalSubnet, local.Origin);
        Assert.Equal("Ethernet 2", local.InterfaceName);
        Assert.Equal(ScanTargetOrigin.RoutedSubnet, routed.Origin);
        Assert.Equal("wt0", routed.InterfaceName);
    }

    [Fact]
    public void LocalSubnetsComeFirst()
    {
        // Reversed input, so ordering can't pass by accident.
        var targets = ScanTargetPlanner.Plan(RealWorldTable().Reverse(), ["192.168.1.160"]);

        Assert.Equal(ScanTargetOrigin.LocalSubnet, targets[0].Origin);
    }

    [Theory]
    [InlineData("0.0.0.0", 0)]          // default route — would be the whole internet
    [InlineData("10.0.0.0", 8)]         // wider than the ceiling
    [InlineData("192.168.1.0", 16)]
    [InlineData("192.168.1.0", 21)]     // just outside /22
    [InlineData("192.168.1.160", 32)]   // host route
    [InlineData("192.168.1.0", 31)]
    public void PrefixesOutsideTheUsefulBand_AreDropped(string destination, int prefix)
    {
        var targets = ScanTargetPlanner.Plan([new RouteEntry(destination, prefix, "eth0")], []);

        Assert.Empty(targets);
    }

    [Theory]
    [InlineData("8.8.8.0")]             // public
    [InlineData("100.85.8.0")]          // CGNAT: a mesh VPN's own carrier network
    [InlineData("169.254.1.0")]         // link-local
    [InlineData("224.0.1.0")]           // multicast
    [InlineData("172.15.0.0")]          // just below the private 172.16/12 block
    [InlineData("172.32.0.0")]          // just above it
    public void NonPrivateDestinations_AreDropped(string destination)
    {
        var targets = ScanTargetPlanner.Plan([new RouteEntry(destination, 24, "eth0")], []);

        Assert.Empty(targets);
    }

    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.0.0")]
    [InlineData("192.168.0.0")]
    public void PrivateDestinations_AreKept(string destination)
    {
        var targets = ScanTargetPlanner.Plan([new RouteEntry(destination, 24, "eth0")], []);

        Assert.Single(targets);
    }

    [Fact]
    public void TheSameSubnetOnSeveralInterfaces_IsListedOnce()
    {
        var targets = ScanTargetPlanner.Plan(
            [
                new RouteEntry("192.168.1.0", 24, "Ethernet 2"),
                new RouteEntry("192.168.1.0", 24, "Wi-Fi"),
            ],
            []);

        Assert.Single(targets);
        Assert.Equal("Ethernet 2", targets[0].InterfaceName);
    }

    [Fact]
    public void RangeCarriesTheUsableHostsOnly()
    {
        var target = ScanTargetPlanner.Plan([new RouteEntry("192.168.1.0", 24, "eth0")], []).Single();

        Assert.Equal(254, target.Range.Count);
        Assert.True(target.Range.Contains("192.168.1.1"));
        Assert.False(target.Range.Contains("192.168.1.0"));
    }

    [Fact]
    public void NoRoutes_YieldsNoTargets()
    {
        Assert.Empty(ScanTargetPlanner.Plan([], ["192.168.1.160"]));
    }
}
