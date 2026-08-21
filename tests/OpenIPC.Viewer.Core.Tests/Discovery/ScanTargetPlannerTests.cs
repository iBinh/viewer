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
        // Reversed input, so ordering can't pass by accident. Enumerable.Reverse,
        // not the array's — on .NET 9 `RouteEntry[].Reverse()` binds to
        // MemoryExtensions.Reverse(Span<T>), which reverses in place and returns void.
        var targets = ScanTargetPlanner.Plan(Enumerable.Reverse(RealWorldTable()), ["192.168.1.160"]);

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

    // IsPrivateIpv4 is also the gate the web scan endpoint puts a typed ipRange
    // through, so the SSRF-relevant addresses are pinned directly.
    [Theory]
    [InlineData("127.0.0.1", false)]        // the server's own loopback services
    [InlineData("169.254.169.254", false)]  // cloud instance metadata
    [InlineData("0.0.0.0", false)]
    [InlineData("8.8.8.8", false)]          // arbitrary public host
    [InlineData("100.64.0.1", false)]       // CGNAT
    [InlineData("192.167.255.255", false)]  // one below 192.168/16
    [InlineData("192.169.0.0", false)]      // one above it
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.3.137", true)]
    public void IsPrivateIpv4_ClassifiesTheSsrfBoundary(string address, bool expected)
    {
        Assert.Equal(expected, ScanTargetPlanner.IsPrivateIpv4(address));
    }

    // The exact composition the web endpoint uses to reject a range that would
    // let a Manage user aim the server's sweep off the LAN: every enumerated
    // host must be private, so a single public address in the range fails it.
    [Theory]
    [InlineData("192.168.1.0/24", true)]
    [InlineData("10.0.0.10-10.0.0.20", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("192.168.1.250-192.168.2.5", true)]   // crosses a /24 but stays private
    [InlineData("192.167.255.250-192.168.0.5", false)] // starts one subnet below private space
    public void WebRangeGate_AcceptsOnlyFullyPrivateRanges(string text, bool expected)
    {
        Assert.True(IpRange.TryParse(text, out var range, out _));
        Assert.Equal(expected, range.EnumerateHosts().All(ScanTargetPlanner.IsPrivateIpv4));
    }
}
