using System;
using System.Collections.Generic;

namespace OpenIPC.Viewer.Core.Onvif;

// What a specific camera's PTZ node can actually do, read once from
// GetConfigurationOptions and GetServiceCapabilities.
//
// PTZ devices differ far more than the spec suggests: some implement only
// continuous velocity moves, some only relative steps; ranges are per-device;
// MoveStatus is optional and several popular brands report it but never update
// it. The UI asks this record what to show rather than offering buttons that
// quietly do nothing.
public sealed record PtzCapabilities(
    bool SupportsContinuous,
    bool SupportsRelative,
    bool SupportsAbsolute,
    bool SupportsHome,
    // GetStatus returns a MoveStatus this camera actually maintains. When
    // false, "has the move finished" can only be answered by waiting.
    bool SupportsMoveStatus,
    // Relative pan/tilt expressed as a fraction of the current field of view
    // (TranslationSpaceFov). This is what makes a fixed step feel the same at
    // wide and at full zoom — without it a step is in device units and gets
    // wilder the further in you are.
    bool RelativeIsFieldOfView,
    PtzRange RelativePan,
    PtzRange RelativeTilt,
    PtzRange RelativeZoom,
    PtzRange AbsoluteZoom,
    IReadOnlyList<string> AuxiliaryCommands)
{
    // What a camera that answered nothing useful gets: continuous only, the one
    // operation nearly every PTZ device implements, and what this app assumed
    // of every camera before it started asking.
    public static PtzCapabilities ContinuousOnly { get; } = new(
        SupportsContinuous: true,
        SupportsRelative: false,
        SupportsAbsolute: false,
        SupportsHome: false,
        SupportsMoveStatus: false,
        RelativeIsFieldOfView: false,
        RelativePan: PtzRange.Normalized,
        RelativeTilt: PtzRange.Normalized,
        RelativeZoom: PtzRange.Normalized,
        AbsoluteZoom: PtzRange.Normalized,
        AuxiliaryCommands: Array.Empty<string>());
}
