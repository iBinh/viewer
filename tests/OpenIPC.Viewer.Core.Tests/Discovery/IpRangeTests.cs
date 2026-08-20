using System.Linq;
using OpenIPC.Viewer.Core.Discovery;
using Xunit;

namespace OpenIPC.Viewer.Core.Tests.Discovery;

// The hand-typed sweep range. Pure parsing, so no sockets — and worth pinning
// down hard: every one of these strings decides which hosts get knocked on, and
// a silently-misparsed range reaches the user as "no cameras found".
public sealed class IpRangeTests
{
    [Fact]
    public void SingleAddress_IsOneHost()
    {
        var range = Parse("192.168.1.64");

        Assert.Equal(1, range.Count);
        Assert.Equal(new[] { "192.168.1.64" }, range.EnumerateHosts().ToArray());
    }

    [Fact]
    public void Cidr24_DropsNetworkAndBroadcast()
    {
        var range = Parse("192.168.1.0/24");
        var hosts = range.EnumerateHosts().ToList();

        Assert.Equal(254, range.Count);
        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0]);
        Assert.Equal("192.168.1.254", hosts[^1]);
    }

    [Fact]
    public void Cidr_NormalizesFromAnyAddressInsideIt()
    {
        // "the /24 my camera is on", typed as the camera's own address.
        Assert.Equal("192.168.1.1-192.168.1.254", Parse("192.168.1.77/24").ToString());
    }

    [Theory]
    [InlineData("10.1.2.3/32", 1)]   // no spare network/broadcast to drop
    [InlineData("10.1.2.2/31", 2)]   // point-to-point: both addresses are usable
    [InlineData("10.1.2.0/30", 2)]   // 4 addresses, 2 of them usable
    [InlineData("10.1.2.0/28", 14)]
    public void Cidr_HostCountMatchesTheUsableAddresses(string text, int expected)
    {
        var range = Parse(text);

        Assert.Equal(expected, range.Count);
        Assert.Equal(expected, range.EnumerateHosts().Count());
    }

    [Fact]
    public void ExplicitRange_IsInclusiveAtBothEnds()
    {
        Assert.Equal(
            new[] { "192.168.1.10", "192.168.1.11", "192.168.1.12" },
            Parse("192.168.1.10-192.168.1.12").EnumerateHosts().ToArray());
    }

    [Fact]
    public void ShorthandRange_ReplacesTheLastOctetOnly()
    {
        Assert.Equal(
            new[] { "192.168.1.10", "192.168.1.11", "192.168.1.12" },
            Parse("192.168.1.10-12").EnumerateHosts().ToArray());
    }

    [Fact]
    public void RangeCrossingAnOctetBoundary_Walks()
    {
        Assert.Equal(
            new[] { "10.0.0.254", "10.0.0.255", "10.0.1.0", "10.0.1.1" },
            Parse("10.0.0.254-10.0.1.1").EnumerateHosts().ToArray());
    }

    [Theory]
    [InlineData("192.168.1.10, 192.168.1.20")]
    [InlineData("192.168.1.10;192.168.1.20")]
    [InlineData("  192.168.1.10   192.168.1.20 ")]
    public void SeveralEntries_MayBeSeparatedByCommaSemicolonOrSpace(string text)
    {
        Assert.Equal(new[] { "192.168.1.10", "192.168.1.20" }, Parse(text).EnumerateHosts().ToArray());
    }

    [Fact]
    public void OverlappingEntries_AreMergedSoNoHostIsProbedTwice()
    {
        var range = Parse("192.168.1.0/24 192.168.1.50");

        Assert.Equal(254, range.Count);
        Assert.Equal(254, range.EnumerateHosts().Distinct().Count());
    }

    [Fact]
    public void AdjacentEntries_FoldIntoOneSpan()
    {
        var range = Parse("192.168.1.1-192.168.1.10, 192.168.1.11-192.168.1.20");

        Assert.Equal(20, range.Count);
        Assert.Equal("192.168.1.1-192.168.1.20", range.ToString());
    }

    [Fact]
    public void DisjointEntries_StayApartAndSortAscending()
    {
        var range = Parse("10.0.5.5, 10.0.0.1-10.0.0.2");

        Assert.Equal("10.0.0.1-10.0.0.2, 10.0.5.5", range.ToString());
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2", "10.0.5.5" }, range.EnumerateHosts().ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankInput_ReportsEmptyRatherThanMalformed(string? text)
    {
        // Callers read Empty as "nothing typed — sweep the local subnet", so it
        // must not come back looking like a typo.
        Assert.False(IpRange.TryParse(text, out var range, out var error));
        Assert.Null(range);
        Assert.Equal(IpRangeParseError.Empty, error);
    }

    [Theory]
    [InlineData("192.168.1")]           // shorthand quad — IPAddress.TryParse would take this
    [InlineData("192.168.1.1.1")]
    [InlineData("192.168.1.256")]
    [InlineData("192.168.1.-5")]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1.0/")]
    [InlineData("192.168.1.0/abc")]
    [InlineData("1.2.3.4-1.2.3.3")]     // end below start
    [InlineData("1.2.3.10-9")]          // the same, in shorthand form
    [InlineData("1.2.3.4-1.2.3.300")]
    [InlineData("192.168.1.0/24 nonsense")]
    [InlineData("camera.local")]
    [InlineData("fe80::1")]             // IPv4 only for now
    public void MalformedInput_IsRejected(string text)
    {
        Assert.False(IpRange.TryParse(text, out var range, out var error));
        Assert.Null(range);
        Assert.Equal(IpRangeParseError.Malformed, error);
    }

    [Fact]
    public void RangeWiderThanTheCeiling_IsRejectedInsteadOfSweptForMinutes()
    {
        Assert.False(IpRange.TryParse("10.0.0.0/16", out var range, out var error));
        Assert.Null(range);
        Assert.Equal(IpRangeParseError.TooLarge, error);
    }

    [Fact]
    public void RangeAtTheCeiling_IsAccepted()
    {
        // /20 is 4096 addresses, 4094 of them usable — the widest single entry
        // that still fits under MaxHosts.
        var range = Parse("10.0.0.0/20");

        Assert.Equal(4094, range.Count);
        Assert.True(range.Count <= IpRange.MaxHosts);
    }

    [Fact]
    public void SeveralEntriesTogether_AreCappedByTheSameCeiling()
    {
        Assert.False(IpRange.TryParse("10.0.0.0/20, 10.1.0.0/20", out _, out var error));
        Assert.Equal(IpRangeParseError.TooLarge, error);
    }

    [Fact]
    public void HighestAddress_DoesNotOverflowTheWalk()
    {
        Assert.Equal(
            new[] { "255.255.255.253", "255.255.255.254", "255.255.255.255" },
            Parse("255.255.255.253-255.255.255.255").EnumerateHosts().ToArray());
    }

    [Fact]
    public void Combine_FoldsSeveralRangesAndDropsTheOverlap()
    {
        // What the dialog does with the ticked subnets plus a typed range.
        var combined = Combine(Parse("192.168.1.0/24"), Parse("192.168.3.0/24"), Parse("192.168.1.50"));

        Assert.Equal(508, combined.Count);
        Assert.Equal("192.168.1.1-192.168.1.254, 192.168.3.1-192.168.3.254", combined.ToString());
    }

    [Fact]
    public void Combine_OfNothing_ReportsEmpty()
    {
        Assert.False(IpRange.TryCombine([], out var combined, out var error));
        Assert.Null(combined);
        Assert.Equal(IpRangeParseError.Empty, error);
    }

    [Fact]
    public void Combine_IsCappedLikeASingleTypedRange()
    {
        // Ticking enough subnets has to hit the same ceiling as typing them.
        var wide = Enumerable.Range(0, 20).Select(i => Parse($"10.{i}.0.0/22")).ToList();

        Assert.False(IpRange.TryCombine(wide, out _, out var error));
        Assert.Equal(IpRangeParseError.TooLarge, error);
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.168.1.254", true)]
    [InlineData("192.168.1.0", false)]    // network address, excluded from a /24
    [InlineData("192.168.1.255", false)]  // broadcast, likewise
    [InlineData("192.168.2.1", false)]
    [InlineData("not-an-address", false)]
    public void Contains_AnswersForTheUsableHostsOnly(string address, bool expected)
    {
        Assert.Equal(expected, Parse("192.168.1.0/24").Contains(address));
    }

    private static IpRange Combine(params IpRange[] ranges)
    {
        Assert.True(IpRange.TryCombine(ranges, out var combined, out var error), $"combine failed: {error}");
        Assert.NotNull(combined);
        return combined!;
    }

    // Parse-or-fail-the-test, so each case reads as the thing it is about — and
    // hands back a non-null range, which TryParse's out param isn't on its own.
    private static IpRange Parse(string text)
    {
        Assert.True(IpRange.TryParse(text, out var range, out var error), $"'{text}' should parse, got {error}");
        Assert.NotNull(range);
        return range!;
    }
}
