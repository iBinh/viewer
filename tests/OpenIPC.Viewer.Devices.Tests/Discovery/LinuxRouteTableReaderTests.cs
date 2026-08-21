using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Devices.Discovery.Routes;
using Xunit;

namespace OpenIPC.Viewer.Devices.Tests.Discovery;

// The /proc/net/route parse. Its whole job is byte-order arithmetic on hex
// words, which is exactly the sort of thing that is right until it silently
// isn't — so it is pinned with real kernel output rather than trusted.
public sealed class LinuxRouteTableReaderTests
{
    // Verbatim from a Linux host: header, a default route, and two subnet routes.
    // Destination/Mask are little-endian hex, so 0050A8C0 is the bytes C0 A8 50 00
    // = 192.168.80.0, and mask 00F0FFFF is FF FF F0 00 = /20.
    private static readonly string[] ProcNetRoute =
    {
        "Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT",
        "eth0\t00000000\t0150A8C0\t0003\t0\t0\t0\t00000000\t0\t0\t0",     // default route, mask /0
        "eth0\t0050A8C0\t00000000\t0001\t0\t0\t0\t00F0FFFF\t0\t0\t0",     // 192.168.80.0/20
        "eth0\t0003A8C0\t00000000\t0001\t0\t0\t0\t00FFFFFF\t0\t0\t0",     // 192.168.3.0/24
    };

    [Fact]
    public void Parse_DecodesLittleEndianDestinationsAndMasks()
    {
        var routes = LinuxRouteTableReader.Parse(ProcNetRoute);

        Assert.Equal(3, routes.Count);
        Assert.Equal(new RouteEntry("0.0.0.0", 0, "eth0"), routes[0]);
        Assert.Equal(new RouteEntry("192.168.80.0", 20, "eth0"), routes[1]);
        Assert.Equal(new RouteEntry("192.168.3.0", 24, "eth0"), routes[2]);
    }

    [Fact]
    public void Parse_SkipsTheHeaderRow()
    {
        // The header's "Destination"/"Mask" are not hex, so the hex parse drops
        // the row without any special-casing — this proves it.
        var routes = LinuxRouteTableReader.Parse(ProcNetRoute);

        Assert.DoesNotContain(routes, r => r.InterfaceName == "Iface");
    }

    [Fact]
    public void Parse_DropsRowsWithTooFewColumns()
    {
        var routes = LinuxRouteTableReader.Parse(new[] { "eth0\t0003A8C0\t00000000" });

        Assert.Empty(routes);
    }

    [Fact]
    public void Parse_DropsNonContiguousMasks()
    {
        // 00FF00FF = FF 00 FF 00 host-order — a mask with a gap, legal to store
        // but meaningless as a prefix length, so it is dropped rather than
        // guessed at.
        var routes = LinuxRouteTableReader.Parse(new[] { "eth0\t0003A8C0\t00000000\t0001\t0\t0\t0\t00FF00FF\t0\t0\t0" });

        Assert.Empty(routes);
    }

    [Fact]
    public void Parse_OfEmptyInput_IsEmpty()
    {
        Assert.Empty(LinuxRouteTableReader.Parse(Array.Empty<string>()));
    }
}
