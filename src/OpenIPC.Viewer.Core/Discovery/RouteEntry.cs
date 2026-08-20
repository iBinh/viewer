namespace OpenIPC.Viewer.Core.Discovery;

// One IPv4 route as the OS reports it, reduced to the three fields the scan
// planner cares about. Platform readers (Windows iphlpapi, Linux /proc/net/route)
// produce these; ScanTargetPlanner turns them into sweepable ranges.
//
// Destination is the network address in dotted-quad form, already masked.
public sealed record RouteEntry(string Destination, int PrefixLength, string InterfaceName);
