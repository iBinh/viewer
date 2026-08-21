using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace OpenIPC.Viewer.Core.Discovery;

// A hand-typed IPv4 sweep range, for when the auto-detected local /24 is the
// wrong one: cameras on a second VLAN, a host with several NICs, or a camera
// parked outside the subnet of the machine running the viewer (the classic
// 192.168.1.10 camera seen from a 192.168.0.x laptop).
//
// Accepted forms, several at a time separated by comma/semicolon/whitespace:
//   192.168.1.0/24               CIDR - network + broadcast dropped for /30 and wider
//   192.168.1.10-192.168.1.200   explicit first-last
//   192.168.1.10-200             shorthand: the tail is just the last octet
//   192.168.1.64                 a single host
//
// Dotted quads are parsed by hand instead of via IPAddress.TryParse on purpose:
// that one also accepts shorthand ("192.168.1" -> 192.168.0.1) and hex/octal
// forms, which would silently turn a typo into a sweep of the wrong subnet.
public sealed class IpRange
{
    // The sweep is one unprivileged TCP connect per port per host, so the ceiling
    // is about wall-clock rather than safety - a /20 already runs for minutes.
    public const int MaxHosts = 4096;

    private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n' };

    // Ascending, non-overlapping, non-touching after Merge.
    private readonly IReadOnlyList<Block> _blocks;

    private IpRange(IReadOnlyList<Block> blocks, int count)
    {
        _blocks = blocks;
        Count = count;
    }

    // How many addresses a scan of this range would knock on.
    public int Count { get; }

    public static bool TryParse(string? text, [NotNullWhen(true)] out IpRange? range, out IpRangeParseError error)
    {
        range = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = IpRangeParseError.Empty;
            return false;
        }

        var blocks = new List<Block>();
        foreach (var token in text!.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseToken(token, out var block))
            {
                error = IpRangeParseError.Malformed;
                return false;
            }

            blocks.Add(block);
        }

        if (blocks.Count == 0)
        {
            error = IpRangeParseError.Empty;
            return false;
        }

        var merged = Merge(blocks);
        // long, because anything wider than a /12 overflows an int host count.
        var count = merged.Sum(b => (long)b.Last - b.First + 1);
        if (count > MaxHosts)
        {
            error = IpRangeParseError.TooLarge;
            return false;
        }

        range = new IpRange(merged, (int)count);
        error = IpRangeParseError.None;
        return true;
    }

    // Fold several ranges into one, under the same ceiling a single typed range
    // gets. Used when the auto-detected subnets and a typed range are swept in
    // one pass — the merge means overlapping ones cost nothing extra.
    public static bool TryCombine(
        IEnumerable<IpRange> ranges, [NotNullWhen(true)] out IpRange? combined, out IpRangeParseError error)
    {
        combined = null;

        var blocks = new List<Block>();
        foreach (var range in ranges)
            blocks.AddRange(range._blocks);

        if (blocks.Count == 0)
        {
            error = IpRangeParseError.Empty;
            return false;
        }

        var merged = Merge(blocks);
        var count = merged.Sum(b => (long)b.Last - b.First + 1);
        if (count > MaxHosts)
        {
            error = IpRangeParseError.TooLarge;
            return false;
        }

        combined = new IpRange(merged, (int)count);
        error = IpRangeParseError.None;
        return true;
    }

    /// <summary>Whether a dotted-quad address falls inside this range.</summary>
    public bool Contains(string address)
    {
        if (!TryParseAddress(address, out var value)) return false;

        foreach (var block in _blocks)
        {
            if (value >= block.First && value <= block.Last) return true;
        }

        return false;
    }

    // Every address in the range, ascending, deduplicated by the merge.
    public IEnumerable<string> EnumerateHosts()
    {
        foreach (var block in _blocks)
        {
            // Not a for(...; value <= Last; value++): Last can be uint.MaxValue,
            // where that condition never goes false.
            var value = block.First;
            while (true)
            {
                yield return Format(value);
                if (value == block.Last) break;
                value++;
            }
        }
    }

    // Normalized form, for status text and logs ("192.168.1.1-192.168.1.254").
    public override string ToString() =>
        string.Join(", ", _blocks.Select(b =>
            b.First == b.Last ? Format(b.First) : $"{Format(b.First)}-{Format(b.Last)}"));

    private static bool TryParseToken(string token, out Block block)
    {
        block = default;

        var slash = token.IndexOf('/');
        if (slash >= 0)
            return TryParseCidr(token.Substring(0, slash), token.Substring(slash + 1), out block);

        var dash = token.IndexOf('-');
        if (dash < 0)
        {
            if (!TryParseAddress(token, out var single)) return false;
            block = new Block(single, single);
            return true;
        }

        if (!TryParseAddress(token.Substring(0, dash), out var first)) return false;

        var tail = token.Substring(dash + 1);
        uint last;
        if (tail.IndexOf('.') >= 0)
        {
            if (!TryParseAddress(tail, out last)) return false;
        }
        else
        {
            // "192.168.1.10-200" - the tail replaces the last octet only.
            if (!TryParseOctet(tail, out var octet)) return false;
            last = (first & 0xFFFFFF00u) | octet;
        }

        if (last < first) return false;

        block = new Block(first, last);
        return true;
    }

    private static bool TryParseCidr(string address, string prefixText, out Block block)
    {
        block = default;

        if (!TryParseAddress(address, out var value)) return false;
        if (!int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)) return false;
        if (prefix < 0 || prefix > 32) return false;

        // Guarded: a 32-bit shift by 32 is a no-op in C#, not zero.
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = value & mask;
        var broadcast = network | ~mask;

        // /31 and /32 have no spare network/broadcast address, so they are scanned
        // whole. Wider prefixes skip both - neither is ever a camera.
        block = prefix >= 31 ? new Block(network, broadcast) : new Block(network + 1, broadcast - 1);
        return true;
    }

    private static bool TryParseAddress(string text, out uint value)
    {
        value = 0;

        var parts = text.Split('.');
        if (parts.Length != 4) return false;

        foreach (var part in parts)
        {
            if (!TryParseOctet(part, out var octet)) return false;
            value = (value << 8) | octet;
        }

        return true;
    }

    // NumberStyles.None rejects signs, whitespace and thousands separators, so
    // "-5" / " 5" / "1,0" never sneak through as a valid octet.
    private static bool TryParseOctet(string text, out uint octet)
    {
        octet = 0;
        if (text.Length == 0 || text.Length > 3) return false;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        if (parsed > 255) return false;

        octet = (uint)parsed;
        return true;
    }

    // Fold overlapping and adjacent blocks together so "192.168.1.0/24 192.168.1.50"
    // is 254 hosts, not 255 with one of them probed twice.
    private static List<Block> Merge(List<Block> blocks)
    {
        var merged = new List<Block>(blocks.Count);
        foreach (var block in blocks.OrderBy(b => b.First))
        {
            if (merged.Count > 0)
            {
                var previous = merged[merged.Count - 1];
                // "Last + 1" would overflow at 255.255.255.255 - a block ending
                // there already swallows anything that could follow it.
                if (previous.Last == uint.MaxValue || block.First <= previous.Last + 1)
                {
                    merged[merged.Count - 1] = new Block(previous.First, Math.Max(previous.Last, block.Last));
                    continue;
                }
            }

            merged.Add(block);
        }

        return merged;
    }

    private static string Format(uint value) =>
        $"{value >> 24}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    // An inclusive run of addresses as raw host-order uints.
    private readonly struct Block
    {
        public Block(uint first, uint last)
        {
            First = first;
            Last = last;
        }

        public uint First { get; }
        public uint Last { get; }
    }
}
