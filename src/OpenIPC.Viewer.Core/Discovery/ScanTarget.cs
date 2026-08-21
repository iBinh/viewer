namespace OpenIPC.Viewer.Core.Discovery;

// A subnet the sweep could usefully scan, worked out from the OS routing table
// instead of typed by the user. Cidr and InterfaceName are for the UI to label
// the row with; Range is what the sweep actually walks.
public sealed record ScanTarget(
    IpRange Range,
    string Cidr,
    string InterfaceName,
    ScanTargetOrigin Origin);

public enum ScanTargetOrigin
{
    // This machine holds an address inside the subnet - the ordinary LAN case.
    LocalSubnet = 0,

    // Reachable only through a route: another VLAN, or a VPN/mesh tunnel. These
    // are the ones passive discovery can never see, because multicast stops at
    // the router.
    RoutedSubnet,
}
