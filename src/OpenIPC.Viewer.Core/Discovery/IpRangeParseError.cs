namespace OpenIPC.Viewer.Core.Discovery;

// Why IpRange.TryParse rejected the text. A code rather than a message because
// Core carries no UI strings — each front-end maps this to its own localized
// text (the desktop dialog to Localizer keys, the web API to a validation body).
public enum IpRangeParseError
{
    None = 0,

    // Nothing but whitespace/separators — the caller decides whether that means
    // "invalid" or "fall back to the auto-detected subnet".
    Empty,

    // Not one of the accepted forms, or an octet/prefix out of range, or an
    // end address below its start.
    Malformed,

    // Parses fine but covers more than IpRange.MaxHosts addresses.
    TooLarge,
}
