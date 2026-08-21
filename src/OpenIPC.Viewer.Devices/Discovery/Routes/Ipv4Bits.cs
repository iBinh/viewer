namespace OpenIPC.Viewer.Devices.Discovery.Routes;

// Bit twiddling shared by the platform route readers. Endian-independent on
// purpose: both OSes hand back addresses as raw 32-bit words, and doing the
// conversion arithmetically rather than through BitConverter keeps the readers
// correct wherever .NET runs.
internal static class Ipv4Bits
{
    /// <summary>
    /// Reinterpret a word whose bytes are in network order (as stored by the OS)
    /// into a host-order value where the first octet is the high byte.
    /// </summary>
    public static uint FromNetworkOrder(uint value) =>
        (value << 24)
        | ((value & 0x0000FF00u) << 8)
        | ((value >> 8) & 0x0000FF00u)
        | (value >> 24);

    public static string Format(uint hostOrder) =>
        $"{hostOrder >> 24}.{(hostOrder >> 16) & 0xFF}.{(hostOrder >> 8) & 0xFF}.{hostOrder & 0xFF}";

    /// <summary>
    /// Prefix length of a subnet mask, or -1 when the mask is non-contiguous
    /// (legal in the API, meaningless as CIDR, so such a route is dropped).
    /// </summary>
    public static int PrefixLengthFromMask(uint mask)
    {
        var prefix = 0;
        while (prefix < 32 && (mask & 0x80000000u) != 0)
        {
            mask <<= 1;
            prefix++;
        }

        // Anything still set after the leading run means gaps in the mask.
        return mask == 0 ? prefix : -1;
    }
}
