using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Discovery;

namespace OpenIPC.Viewer.Devices.Discovery.Routes;

// /proc/net/route, the kernel's IPv4 routing table as text:
//
//   Iface  Destination  Gateway   Flags  RefCnt  Use  Metric  Mask      ...
//   eth0   00000000     0150A8C0  0003   0       0    0       00000000
//   eth0   0050A8C0     00000000  0001   0       0    0       00F0FFFF
//
// Destination and Mask are the raw 32-bit words in network byte order, printed
// as hex — so 0050A8C0 is the word whose bytes are C0 A8 50 00, i.e. 192.168.80.0.
// Ipv4Bits.FromNetworkOrder does that flip, exactly as for the Windows table.
//
// Deliberately NOT used on Android: /proc/net access has been restricted to
// system apps since Android 10, so the read would fail there anyway and the
// provider falls back to interface subnets.
public sealed class LinuxRouteTableReader : IRouteTableReader
{
    private const string RouteFile = "/proc/net/route";
    private const int DestinationField = 1;
    private const int MaskField = 7;

    private readonly ILogger<LinuxRouteTableReader> _logger;

    public LinuxRouteTableReader(ILogger<LinuxRouteTableReader> logger) => _logger = logger;

    public IReadOnlyList<RouteEntry> Read()
    {
        try
        {
            return File.Exists(RouteFile)
                ? Parse(File.ReadAllLines(RouteFile))
                : Array.Empty<RouteEntry>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Reading {File} failed", RouteFile);
            return Array.Empty<RouteEntry>();
        }
    }

    // Header and any malformed line are skipped by the hex parse failing, so
    // there is no need to special-case the first row.
    public static IReadOnlyList<RouteEntry> Parse(IEnumerable<string> lines)
    {
        var routes = new List<RouteEntry>();

        foreach (var line in lines)
        {
            var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= MaskField) continue;

            if (!TryParseHexWord(fields[DestinationField], out var destination)) continue;
            if (!TryParseHexWord(fields[MaskField], out var mask)) continue;

            var prefix = Ipv4Bits.PrefixLengthFromMask(Ipv4Bits.FromNetworkOrder(mask));
            if (prefix < 0) continue;

            routes.Add(new RouteEntry(
                Ipv4Bits.Format(Ipv4Bits.FromNetworkOrder(destination)),
                prefix,
                fields[0]));
        }

        return routes;
    }

    private static bool TryParseHexWord(string text, out uint value) =>
        uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
}
