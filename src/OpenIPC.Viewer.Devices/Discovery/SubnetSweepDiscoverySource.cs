using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Majestic;
using OpenIPC.Viewer.Core.Onvif;
using OpenIPC.Viewer.Core.Onvif.Discovery;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Settings;

namespace OpenIPC.Viewer.Devices.Discovery;

// Active sweep — the only source that finds OpenIPC cameras running neither
// ONVIF nor mDNS. Opt-in (DeepScan, or a hand-typed range) because it knocks on
// every host of the LAN.
//
// Cross-platform by construction: unprivileged TCP connects + HTTP only (no raw
// sockets/root), and the subnet is derived from the local IPv4 as a plain /24 —
// no GatewayAddresses / IPv4Mask (both throw on Android's BCL). Capped at 254
// hosts on purpose: it never sweeps a /16 by itself. DiscoveryOptions.Range
// overrides that derived subnet when the user knows better — a camera on another
// VLAN, or a host whose "own" subnet isn't the camera one — and carries its own
// ceiling (IpRange.MaxHosts).
public sealed class SubnetSweepDiscoverySource : IDiscoverySource
{
    private const int MaxConcurrency = 64;
    private static readonly TimeSpan PortTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly int[] RtspPorts = { 554, 8554 };
    private static readonly int[] HttpPorts = { 80, 8080 };

    private readonly INetworkInterfaceProvider _nics;
    private readonly IUserSettingsAccessor _settings;
    private readonly IReachabilityProbe _probe;
    private readonly IMajesticClient _majestic;
    private readonly IOnvifFingerprint _onvif;
    private readonly ILogger<SubnetSweepDiscoverySource> _logger;

    public SubnetSweepDiscoverySource(
        INetworkInterfaceProvider nics,
        IUserSettingsAccessor settings,
        IReachabilityProbe probe,
        IMajesticClient majestic,
        IOnvifFingerprint onvif,
        ILogger<SubnetSweepDiscoverySource> logger)
    {
        _nics = nics;
        _settings = settings;
        _probe = probe;
        _majestic = majestic;
        _onvif = onvif;
        _logger = logger;
    }

    public string Name => "Subnet sweep";

    public bool IsEnabled(DiscoveryOptions options) => options.DeepScan || options.Range is not null;

    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        DiscoveryOptions options, IProgress<double>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        var hosts = ResolveHosts(options);
        if (hosts.Count == 0)
        {
            progress?.Report(1.0);
            yield break;
        }

        var channel = Channel.CreateUnbounded<DiscoveredDevice>();
        var gate = new SemaphoreSlim(MaxConcurrency);
        var done = 0;

        var producer = Task.Run(async () =>
        {
            var probes = hosts.Select(async host =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var device = await ProbeHostAsync(host, ct).ConfigureAwait(false);
                    if (device is not null)
                        await channel.Writer.WriteAsync(device, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogDebug(ex, "Sweep probe failed for {Host}", host); }
                finally
                {
                    gate.Release();
                    progress?.Report((double)Interlocked.Increment(ref done) / hosts.Count);
                }
            });

            try { await Task.WhenAll(probes).ConfigureAwait(false); }
            finally { channel.Writer.TryComplete(); }
        }, ct);

        await foreach (var device in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return device;

        await producer.ConfigureAwait(false);
    }

    // What to knock on: the hand-typed range when there is one, otherwise the
    // local /24 as before. Either way our own address is dropped — it is never
    // a camera, and probing ourselves just wastes a slot.
    private List<string> ResolveHosts(DiscoveryOptions options)
    {
        var local = NetworkInterfaceSelector.ResolveBindAddress(
            _nics.GetCandidates(), _settings.PreferredNetworkInterface);

        if (options.Range is { } range)
        {
            var typed = range.EnumerateHosts()
                .Where(h => !string.Equals(h, local, StringComparison.Ordinal))
                .ToList();
            _logger.LogDebug("Subnet sweep: scanning {Count} host(s) from range {Range}", typed.Count, range);
            return typed;
        }

        if (string.IsNullOrEmpty(local) || !TryGetPrefix(local!, out var prefix, out var ownLast))
        {
            _logger.LogDebug("Subnet sweep: no local /24 to scan; skipping");
            return new List<string>();
        }

        return Enumerable.Range(1, 254)
            .Where(i => i != ownLast)
            .Select(i => $"{prefix}.{i}")
            .ToList();
    }

    // Knock on the RTSP + HTTP ports; an HTTP hit also gets a Majestic and an
    // ONVIF fingerprint. Returns null for a silent host so the aggregator never
    // sees it.
    private async Task<DiscoveredDevice?> ProbeHostAsync(string host, CancellationToken ct)
    {
        var protocols = DiscoveryProtocol.None;
        var ports = new List<int>();
        Uri? onvifUri = null;

        foreach (var port in RtspPorts)
        {
            if (await _probe.IsReachableAsync(host, port, PortTimeout, ct).ConfigureAwait(false))
            {
                protocols |= DiscoveryProtocol.Rtsp;
                ports.Add(port);
            }
        }

        foreach (var port in HttpPorts)
        {
            if (!await _probe.IsReachableAsync(host, port, PortTimeout, ct).ConfigureAwait(false))
                continue;

            protocols |= DiscoveryProtocol.Http;
            ports.Add(port);

            if (!protocols.HasFlag(DiscoveryProtocol.Majestic))
            {
                try
                {
                    if (await _majestic.PingAsync(new MajesticEndpoint(host, port, null), ct).ConfigureAwait(false))
                        protocols |= DiscoveryProtocol.Majestic;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Majestic fingerprint failed for {Host}:{Port}", host, port);
                }
            }

            // WS-Discovery is multicast, so it never reaches a camera on a
            // routed subnet (a VPN/mesh tunnel, another VLAN) — exactly the case
            // the hand-typed range exists for. Asking the address directly is
            // the only way such a camera gets recognised as ONVIF, and carrying
            // the URI back is what lets the add flow run the real probe instead
            // of guessing an RTSP URL.
            onvifUri ??= await _onvif.ProbeAsync(host, port, ct).ConfigureAwait(false);
            if (onvifUri is not null)
                protocols |= DiscoveryProtocol.Onvif;
        }

        if (protocols == DiscoveryProtocol.None)
            return null;
        // A positive fingerprint names the device — the dialog shows the label
        // instead of "(unknown model)". ONVIF gets no label here: its real
        // manufacturer/model arrive with the full probe at add time.
        var model = protocols.HasFlag(DiscoveryProtocol.Majestic) ? "OpenIPC" : null;
        return new DiscoveredDevice(host, protocols, ports, Model: model, OnvifServiceUri: onvifUri);
    }

    private static bool TryGetPrefix(string ip, out string prefix, out int lastOctet)
    {
        prefix = "";
        lastOctet = -1;
        var parts = ip.Split('.');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out lastOctet)) return false;
        prefix = $"{parts[0]}.{parts[1]}.{parts[2]}";
        return true;
    }
}
