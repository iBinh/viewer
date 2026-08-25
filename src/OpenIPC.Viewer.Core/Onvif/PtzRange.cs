namespace OpenIPC.Viewer.Core.Onvif;

// One axis of a camera's PTZ coordinate space, as reported by
// GetConfigurationOptions.
//
// The UI thinks in normalized [-1, 1] (or [0, 1] for absolute zoom); the camera
// thinks in whatever range it declares — commonly [-1, 1], but plenty of
// devices report degrees, [0, 360], or something asymmetric. Sending a value
// outside the declared range is the classic way to have a move silently clamped
// or refused, so every value crossing the wire goes through here.
public readonly record struct PtzRange(float Min, float Max)
{
    public static readonly PtzRange Normalized = new(-1f, 1f);

    public bool IsValid => Max > Min;

    // Maps normalized [-1, 1] onto this range. A symmetric range ([-1,1],
    // [-180,180]) keeps 0 at 0; an asymmetric one maps 0 to its midpoint, which
    // is the only reading under which "no input" still means "centre".
    public float FromNormalized(float value)
    {
        if (!IsValid) return value;
        var clamped = value < -1f ? -1f : value > 1f ? 1f : value;
        var mid = (Max + Min) / 2f;
        var half = (Max - Min) / 2f;
        return mid + clamped * half;
    }

    // Maps normalized [0, 1] onto this range — absolute zoom, where 0 is fully
    // wide and 1 fully tele.
    public float FromUnit(float value)
    {
        if (!IsValid) return value;
        var clamped = value < 0f ? 0f : value > 1f ? 1f : value;
        return Min + clamped * (Max - Min);
    }

    // The inverse, for reporting a camera position back to the UI.
    public float ToUnit(float value)
    {
        if (!IsValid) return value;
        var unit = (value - Min) / (Max - Min);
        return unit < 0f ? 0f : unit > 1f ? 1f : unit;
    }

    public float Clamp(float value) =>
        !IsValid ? value : value < Min ? Min : value > Max ? Max : value;
}
