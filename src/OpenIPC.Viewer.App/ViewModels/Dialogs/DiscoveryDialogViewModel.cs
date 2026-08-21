using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.Core.Discovery;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.App.ViewModels.Dialogs;

// Two-step: (1) scan via the aggregator (ONVIF + later sweep/mDNS) -> merged
// devices, upserted by host as signals arrive; (2) user picks one, types creds,
// we ONVIF-probe (capabilities + profiles + stream URI) to produce the result
// the Library hands to the CameraEditor. Probe fails fast inside the dialog
// instead of pre-filling the editor with bad data.
public sealed partial class DiscoveryDialogViewModel : ViewModelBase
{
    private readonly IDiscoveryAggregator _aggregator;
    private readonly OnvifProbeService _probe;
    private readonly OpenIPC.Viewer.Core.Majestic.IMajesticClient _majestic;
    private readonly DiscoverySessionCache _cache;
    private readonly IReadOnlySet<string> _knownHosts;
    private readonly ILogger<DiscoveryDialogViewModel> _logger;

    private CancellationTokenSource? _scanCts;
    // Cancels in-flight Majestic fingerprints when the dialog goes away.
    private readonly CancellationTokenSource _lifetimeCts = new();
    // What the sweep will actually walk: the ticked subnets folded together with
    // anything typed. Null means "no sweep", which is what an untouched dialog
    // wants — passive sources only.
    private IpRange? _effectiveRange;
    private readonly Dictionary<string, DiscoveredDeviceRowVm> _rowsByHost =
        new(StringComparer.OrdinalIgnoreCase);
    // Hosts we already fingerprinted (or are fingerprinting) — one ping per host.
    private readonly HashSet<string> _fingerprinted = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fingerprintGate = new(6);

    public ObservableCollection<DiscoveredDeviceRowVm> Cameras { get; } = new();

    // Subnets the OS says are reachable, ticked by default — the point is that
    // Deep scan needs no typing to reach a camera on another VLAN or behind a
    // VPN. Empty on a platform with no route reader and no usable interfaces,
    // in which case the dialog shows only the manual box.
    public ObservableCollection<ScanTargetRowVm> ScanTargets { get; } = new();

    public bool HasScanTargets => ScanTargets.Count > 0;

    [ObservableProperty] private string _statusText = Localizer.Instance["Discovery.Status.Initial"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRowSelected))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private DiscoveredDeviceRowVm? _selected;

    public bool IsRowSelected => Selected is not null;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";

    // "Use these credentials for all cameras" (multi-add). Off → the next
    // dialog instance starts with blank login fields.
    [ObservableProperty] private bool _reuseCredentials = true;

    // 0..1 scan progress (mean across sources). Drives the progress bar; hidden
    // when not scanning.
    [ObservableProperty] private double _scanProgress;

    // Opt-in active /24 sweep — finds OpenIPC cameras that answer neither ONVIF
    // nor mDNS, at the cost of knocking on every host. Off by default.
    [ObservableProperty] private bool _deepScan;

    // Extra hand-typed range on top of the ticked subnets — for a subnet with no
    // route of its own, or a single address. Typing one turns the sweep on by
    // itself, so it still works with Deep scan off.
    [ObservableProperty] private string _ipRangeText = "";

    // Null while everything ticked and typed adds up to something sweepable; a
    // localized complaint otherwise. Disables Scan, so neither a typo nor an
    // over-wide selection can quietly degrade into the wrong sweep.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string? _ipRangeError;

    // "will knock on N addresses" — shown before the user commits, because the
    // ticked subnets were chosen for them and the total should not surprise.
    [ObservableProperty] private string _sweepSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _scanInProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _addInProgress;

    public bool CanAdd => Selected is not null && !ScanInProgress && !AddInProgress;

    public DiscoveryDialogViewModel(
        IDiscoveryAggregator aggregator,
        OnvifProbeService probe,
        OpenIPC.Viewer.Core.Majestic.IMajesticClient majestic,
        IScanTargetProvider scanTargets,
        DiscoverySessionCache cache,
        IReadOnlySet<string> knownHosts,
        ILogger<DiscoveryDialogViewModel> logger)
    {
        _aggregator = aggregator;
        _probe = probe;
        _majestic = majestic;
        _cache = cache;
        _knownHosts = knownHosts;
        _logger = logger;

        // Rehydrate the previous scan so the user can add several cameras
        // one-by-one without rescanning between dialog opens. Credentials only
        // carry over while "use for all cameras" is on — a mixed-credential
        // park starts each camera with blank fields.
        _reuseCredentials = cache.ReuseCredentials;
        if (_reuseCredentials)
        {
            _username = cache.Username;
            _password = cache.Password;
        }
        _deepScan = cache.DeepScan;
        _ipRangeText = cache.IpRangeText;

        // Local subnets ticked, routed ones (behind a VPN / another VLAN) not:
        // sweeping the LAN this machine is on is the feature, but sweeping the
        // far side of a tunnel someone happens to be on shouldn't happen without
        // a deliberate tick. A choice the user already made this session wins
        // over the default.
        foreach (var target in SafeTargets(scanTargets))
        {
            var selected = cache.TargetSelections.TryGetValue(target.Cidr, out var choice)
                ? choice
                : target.Origin == ScanTargetOrigin.LocalSubnet;
            ScanTargets.Add(new ScanTargetRowVm(target, selected, OnTargetToggled));
        }

        // Field init skips the generated On*Changed hooks, so the rehydrated
        // state has to be folded in by hand or the first Scan would ignore it.
        RecomputeRange();
        foreach (var device in cache.Snapshot())
            Upsert(device);
        if (Cameras.Count > 0)
            StatusText = string.Format(Localizer.Instance["Discovery.Status.FoundFormat"], Cameras.Count);
    }

    partial void OnUsernameChanged(string value) => _cache.Username = value;
    partial void OnPasswordChanged(string value) => _cache.Password = value;
    partial void OnDeepScanChanged(bool value)
    {
        _cache.DeepScan = value;
        // Deep scan is the gate, the tick list is the destination — flipping the
        // gate changes what gets swept, so the total has to be recomputed.
        RecomputeRange();
    }
    partial void OnReuseCredentialsChanged(bool value) => _cache.ReuseCredentials = value;

    partial void OnIpRangeTextChanged(string value)
    {
        _cache.IpRangeText = value;
        RecomputeRange();
    }

    private void OnTargetToggled(ScanTargetRowVm row)
    {
        _cache.TargetSelections[row.Target.Cidr] = row.IsSelected;
        RecomputeRange();
    }

    // Folds the ticked subnets and the typed range into the single range the
    // sweep walks, and reports what that adds up to. Runs per keystroke and per
    // tick, so it stays pure arithmetic and never touches the network.
    private void RecomputeRange()
    {
        IpRange? typed = null;
        if (!string.IsNullOrWhiteSpace(IpRangeText)
            && !IpRange.TryParse(IpRangeText, out typed, out var typedError))
        {
            Reject(typedError);
            return;
        }

        var parts = new List<IpRange>();
        if (typed is not null)
            parts.Add(typed);
        // Deep scan decides WHETHER to sweep, the tick list decides WHERE. An
        // untouched dialog therefore stays passive-only, exactly as before.
        if (DeepScan)
            parts.AddRange(ScanTargets.Where(t => t.IsSelected).Select(t => t.Target.Range));

        if (parts.Count == 0)
        {
            _effectiveRange = null;
            // With no targets to offer, Deep scan still sweeps the subnet the
            // source works out for itself — say so, rather than leaving the line
            // blank and letting a 254-host sweep come as a surprise.
            SweepSummary = DeepScan && ScanTargets.Count == 0
                ? Localizer.Instance["Discovery.Sweep.SummaryLocal"]
                : "";
            IpRangeError = null;
            return;
        }

        if (!IpRange.TryCombine(parts, out var combined, out var combineError))
        {
            Reject(combineError);
            return;
        }

        _effectiveRange = combined;
        SweepSummary = string.Format(Localizer.Instance["Discovery.Sweep.SummaryFormat"], combined.Count);
        IpRangeError = null;
    }

    private void Reject(IpRangeParseError error)
    {
        _effectiveRange = null;
        SweepSummary = "";
        IpRangeError = error == IpRangeParseError.TooLarge
            ? string.Format(Localizer.Instance["Discovery.IpRange.TooLargeFormat"], IpRange.MaxHosts)
            : Localizer.Instance["Discovery.IpRange.Invalid"];
    }

    // A provider that trips over an exotic adapter must not stop the dialog from
    // opening — the manual box still works with no targets at all.
    private IReadOnlyList<ScanTarget> SafeTargets(IScanTargetProvider provider)
    {
        try
        {
            return provider.GetTargets();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enumerating scan targets failed");
            return Array.Empty<ScanTarget>();
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        Cameras.Clear();
        _rowsByHost.Clear();
        _fingerprinted.Clear();
        _cache.Clear();
        Selected = null;
        ScanProgress = 0;
        StatusText = _effectiveRange is { } sweep
            ? string.Format(Localizer.Instance["Discovery.Status.ScanningRangeFormat"], sweep, sweep.Count)
            : Localizer.Instance["Discovery.Status.Scanning"];
        ScanInProgress = true;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        try
        {
            // Unticking every target means "don't sweep", and has to be passed
            // as such: the sweep source enables on DeepScan alone and would fall
            // back to deriving the local /24, running a 254-host sweep the
            // dialog just told the user it would not run. When no targets were
            // offered at all there is nothing to untick, so Deep scan keeps its
            // original meaning of "sweep whatever subnet you can work out".
            var doSweep = DeepScan && (_effectiveRange is not null || ScanTargets.Count == 0);
            var options = new DiscoveryOptions(TimeSpan.FromSeconds(6), doSweep, _effectiveRange);
            var progress = new Progress<double>(p => ScanProgress = p);

            await foreach (var device in _aggregator.ScanAsync(options, progress, ct).ConfigureAwait(true))
                Upsert(device);

            StatusText = Cameras.Count == 0
                ? Localizer.Instance["Discovery.Status.NoResponse"]
                : string.Format(Localizer.Instance["Discovery.Status.FoundFormat"], Cameras.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = Localizer.Instance["Discovery.Status.Cancelled"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery scan failed");
            StatusText = string.Format(Localizer.Instance["Discovery.Status.ScanFailedFormat"], ex.Message);
        }
        finally
        {
            ScanInProgress = false;
            ScanProgress = 0;
        }
    }

    // Merge-by-host upsert: a device can be yielded repeatedly as more sources
    // confirm it, so update the existing row in place instead of duplicating.
    private void Upsert(DiscoveredDevice device)
    {
        if (_rowsByHost.TryGetValue(device.Host, out var row))
        {
            row.Device = device;
        }
        else
        {
            row = new DiscoveredDeviceRowVm(device)
            {
                IsAlreadyAdded = _knownHosts.Contains(device.Host),
            };
            _rowsByHost[device.Host] = row;
            Cameras.Add(row);
        }

        _cache.Put(row.Device);
        ScheduleFingerprint(row.Device);
    }

    // Newer OpenIPC firmwares always run the Majestic web UI, so an HTTP ping
    // identifies them even when they answer neither ONVIF nor mDNS with a
    // model. One bounded background ping per host; on a hit the row upgrades
    // in place (Majestic protocol + "OpenIPC" label + High confidence).
    private void ScheduleFingerprint(DiscoveredDevice device)
    {
        if (device.Protocols.HasFlag(DiscoveryProtocol.Majestic) && device.Model is not null)
            return;
        if (!_fingerprinted.Add(device.Host))
            return;

        var ct = _lifetimeCts.Token;
        _ = Task.Run(async () =>
        {
            await _fingerprintGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var hit = device.Protocols.HasFlag(DiscoveryProtocol.Majestic);
                var ports = device.Ports.Where(p => p is 80 or 8080).DefaultIfEmpty(80);
                foreach (var port in ports)
                {
                    if (hit) break;
                    hit = await _majestic.PingAsync(
                        new OpenIPC.Viewer.Core.Majestic.MajesticEndpoint(device.Host, port, null), ct).ConfigureAwait(false);
                }
                if (!hit) return;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_rowsByHost.TryGetValue(device.Host, out var row)) return;
                    row.Device = row.Device.MergeWith(new DiscoveredDevice(
                        device.Host, DiscoveryProtocol.Majestic, Array.Empty<int>(), Model: "OpenIPC"));
                    _cache.Put(row.Device);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Majestic fingerprint failed for {Host}", device.Host);
            }
            finally
            {
                _fingerprintGate.Release();
            }
        }, ct);
    }

    private bool CanScan() => !ScanInProgress && !AddInProgress && IpRangeError is null;

    public async Task<DiscoveryDialogResult?> AddSelectedAsync()
    {
        var row = Selected;
        if (row is null) return null;

        var creds = string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password)
            ? null
            : new CameraCredentials(Username, Password);

        AddInProgress = true;
        try
        {
            // ONVIF device → probe for the real stream URI. Non-ONVIF (sweep/mDNS)
            // → skip the probe and pre-fill a guessed RTSP URL from the open ports;
            // the user reviews / tests it in the editor before saving.
            var onvifUri = row.Device.OnvifServiceUri;
            if (onvifUri is null)
            {
                StatusText = Localizer.Instance["Discovery.Status.ManualAdd"];
                return new DiscoveryDialogResult(row.Device, GuessRtspUri(row.Device), null, creds);
            }

            StatusText = string.Format(Localizer.Instance["Discovery.Status.ProbingFormat"], row.HostPort);
            var endpoint = new OnvifEndpoint(onvifUri, creds);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var probeResult = await _probe.ProbeAsync(endpoint, cts.Token).ConfigureAwait(true);

            StatusText = string.Format(Localizer.Instance["Discovery.Status.ProbeOkFormat"], probeResult.Manufacturer ?? "?", probeResult.Model ?? "").TrimEnd();
            return new DiscoveryDialogResult(row.Device, probeResult.RtspMainUri, probeResult, creds);
        }
        catch (OperationCanceledException)
        {
            StatusText = Localizer.Instance["Discovery.Status.Cancelled"];
            return null;
        }
        catch (Exception ex)
        {
            // The ONVIF SOAP probe can't run on every platform — the WCF
            // XmlSerializer stack fails to build on Android (XmlType reflection
            // error over the generated contract types). Rather than dead-end the
            // user, degrade to the same guessed-RTSP add we use for non-ONVIF
            // finds: the camera is added and refined/tested in the editor.
            _logger.LogWarning(ex, "ONVIF probe failed for {Host}; falling back to guessed RTSP", row.HostPort);
            StatusText = Localizer.Instance["Discovery.Status.ManualAdd"];
            return new DiscoveryDialogResult(row.Device, GuessRtspUri(row.Device), null, creds);
        }
        finally
        {
            AddInProgress = false;
        }
    }

    // OpenIPC/Majestic RTSP convention (matches CameraEditor): rtsp://host/ for
    // the default 554, an explicit port otherwise. The user can refine it.
    private static Uri GuessRtspUri(DiscoveredDevice device)
    {
        var port = device.Ports.Contains(8554) && !device.Ports.Contains(554) ? 8554 : 554;
        return port == 554
            ? new Uri($"rtsp://{device.Host}/")
            : new Uri($"rtsp://{device.Host}:{port}/");
    }

    public void Cancel()
    {
        _scanCts?.Cancel();
        _lifetimeCts.Cancel();
    }
}

// One auto-detected subnet in the tick list. The callback (rather than the VM
// watching the collection) keeps the "remember what I unticked" bookkeeping in
// one place and avoids a CollectionChanged subscription per row.
public sealed partial class ScanTargetRowVm : ViewModelBase
{
    private readonly Action<ScanTargetRowVm> _onToggled;

    public ScanTargetRowVm(ScanTarget target, bool isSelected, Action<ScanTargetRowVm> onToggled)
    {
        Target = target;
        _isSelected = isSelected;
        _onToggled = onToggled;
    }

    public ScanTarget Target { get; }

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onToggled(this);

    public string Cidr => Target.Cidr;

    // e.g. "Ethernet 2 · this machine's network · 254 addresses" — the origin is
    // the part worth reading: a routed subnet is one passive discovery can never
    // see, which is exactly why it is offered here.
    public string Detail => string.Format(
        Localizer.Instance["Discovery.Target.DetailFormat"],
        Target.InterfaceName,
        Localizer.Instance[Target.Origin == ScanTargetOrigin.LocalSubnet
            ? "Discovery.Target.Local"
            : "Discovery.Target.Routed"],
        Target.Range.Count);
}

public sealed partial class DiscoveredDeviceRowVm : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(HostPort))]
    [NotifyPropertyChangedFor(nameof(ProtocolsText))]
    [NotifyPropertyChangedFor(nameof(ConfidenceText))]
    private DiscoveredDevice _device;

    // A camera with this host already exists in the library — shown as a badge
    // so the multi-add flow makes it obvious what's left to add.
    [ObservableProperty] private bool _isAlreadyAdded;

    public DiscoveredDeviceRowVm(DiscoveredDevice device) => _device = device;

    public string Host => Device.Host;
    public string DisplayName => Device.Name ?? Device.Model ?? Device.Host;
    public string Subtitle => Device.Model ?? Localizer.Instance["Discovery.UnknownModel"];

    public string HostPort
    {
        get
        {
            var port = Device.OnvifServiceUri?.Port ?? (Device.Ports.Count > 0 ? Device.Ports.First() : 0);
            return port is 0 or 80 ? Device.Host : $"{Device.Host}:{port}";
        }
    }

    // e.g. "ONVIF · RTSP" — how the device was detected.
    public string ProtocolsText => string.Join(" · ", DescribeProtocols(Device.Protocols));

    public string ConfidenceText => Localizer.Instance[Device.Confidence switch
    {
        DiscoveryConfidence.High => "Discovery.Confidence.High",
        DiscoveryConfidence.Medium => "Discovery.Confidence.Medium",
        _ => "Discovery.Confidence.Low",
    }];

    private static IEnumerable<string> DescribeProtocols(DiscoveryProtocol p)
    {
        if (p.HasFlag(DiscoveryProtocol.Onvif)) yield return "ONVIF";
        if (p.HasFlag(DiscoveryProtocol.Mdns)) yield return "mDNS";
        if (p.HasFlag(DiscoveryProtocol.Majestic)) yield return "Majestic";
        if (p.HasFlag(DiscoveryProtocol.Rtsp)) yield return "RTSP";
        if (p.HasFlag(DiscoveryProtocol.Http)) yield return "HTTP";
    }
}

// The dialog's output: the picked device, the RTSP URL to pre-fill (from the
// ONVIF probe when available, otherwise a sensible guess for non-ONVIF devices),
// the ONVIF probe result (null for non-ONVIF — no PTZ/profile metadata), and any
// credentials the user typed.
public sealed record DiscoveryDialogResult(
    DiscoveredDevice Device,
    Uri RtspMainUri,
    OnvifProbeResult? Probe,
    CameraCredentials? Credentials);
