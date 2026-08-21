using System.Collections.Generic;
using OpenIPC.Viewer.Core.Discovery;

namespace OpenIPC.Viewer.Devices.Discovery.Routes;

// Reads the OS IPv4 routing table. There is no managed API for this, so every
// platform needs its own implementation; unsupported ones return nothing and the
// caller falls back to the local interface subnets.
//
// Never throws: a route table that cannot be read is "no extra targets", not an
// error the discovery dialog should surface.
public interface IRouteTableReader
{
    IReadOnlyList<RouteEntry> Read();
}
