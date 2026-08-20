using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Discovery;

namespace OpenIPC.Viewer.Devices.Discovery.Routes;

// iphlpapi's GetIpForwardTable. The older IPv4-only call on purpose: its rows
// are a flat run of 32-bit words, where GetIpForwardTable2 would mean marshalling
// the SOCKADDR_INET union for no gain — discovery is IPv4 throughout.
[SupportedOSPlatform("windows")]
public sealed class WindowsRouteTableReader : IRouteTableReader
{
    private const int NoError = 0;
    private const int ErrorInsufficientBuffer = 122;

    private readonly ILogger<WindowsRouteTableReader> _logger;

    public WindowsRouteTableReader(ILogger<WindowsRouteTableReader> logger) => _logger = logger;

    public IReadOnlyList<RouteEntry> Read()
    {
        try
        {
            return ReadCore();
        }
        catch (Exception ex)
        {
            // A missing export or a marshalling surprise must not take discovery
            // down with it — the caller just loses the routed subnets.
            _logger.LogDebug(ex, "Reading the Windows route table failed");
            return Array.Empty<RouteEntry>();
        }
    }

    private IReadOnlyList<RouteEntry> ReadCore()
    {
        // Standard two-call pattern: ask for the size, then for the table.
        var size = 0;
        if (GetIpForwardTable(IntPtr.Zero, ref size, false) != ErrorInsufficientBuffer)
            return Array.Empty<RouteEntry>();

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpForwardTable(buffer, ref size, false) != NoError)
                return Array.Empty<RouteEntry>();

            // MIB_IPFORWARDTABLE is { DWORD dwNumEntries; MIB_IPFORWARDROW table[]; }
            // and every row field is 4 bytes, so the rows start right after the count.
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpForwardRow>();
            var names = InterfaceNamesByIndex();
            var routes = new List<RouteEntry>(count);

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibIpForwardRow>(IntPtr.Add(buffer, sizeof(int) + (i * rowSize)));
                var prefix = Ipv4Bits.PrefixLengthFromMask(Ipv4Bits.FromNetworkOrder(row.Mask));
                if (prefix < 0)
                    continue;

                routes.Add(new RouteEntry(
                    Ipv4Bits.Format(Ipv4Bits.FromNetworkOrder(row.Dest)),
                    prefix,
                    names.TryGetValue((int)row.IfIndex, out var name) ? name : $"if{row.IfIndex}"));
            }

            return routes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Routes carry an interface index; the dialog wants "Ethernet 2".
    private static Dictionary<int, string> InterfaceNamesByIndex()
    {
        var map = new Dictionary<int, string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                map[nic.GetIPProperties().GetIPv4Properties().Index] = nic.Name;
            }
            catch (NetworkInformationException)
            {
                // An adapter with no IPv4 properties simply has no index to map.
            }
        }

        return map;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpForwardTable(
        IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

    // MIB_IPFORWARDROW. Dest/Mask/NextHop are in network byte order.
    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow
    {
        public uint Dest;
        public uint Mask;
        public uint Policy;
        public uint NextHop;
        public uint IfIndex;
        public uint Type;
        public uint Proto;
        public uint Age;
        public uint NextHopAs;
        public int Metric1;
        public int Metric2;
        public int Metric3;
        public int Metric4;
        public int Metric5;
    }
}
