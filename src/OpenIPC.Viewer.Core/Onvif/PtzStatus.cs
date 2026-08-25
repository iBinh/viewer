using System;

namespace OpenIPC.Viewer.Core.Onvif;

public enum PtzMoveState
{
    // The camera does not report a usable MoveStatus — it is optional in the
    // spec, and several brands return a value that never changes.
    Unknown = 0,
    Idle,
    Moving,
}

// A camera's answer to GetStatus: where it is pointing, and whether it is still
// on its way there. Position is normalized for the UI; the raw device units are
// mapped through the node's declared ranges.
public sealed record PtzStatus(
    float Pan,
    float Tilt,
    float Zoom,
    PtzMoveState PanTilt,
    PtzMoveState ZoomState,
    DateTime? UtcTime)
{
    public bool IsMoving => PanTilt == PtzMoveState.Moving || ZoomState == PtzMoveState.Moving;
}
