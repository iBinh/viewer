using System;
using System.Text;

namespace OpenIPC.Viewer.Core.Onvif;

// Repairs preset names that come back as mojibake.
//
// Cameras routinely store preset names as UTF-8 bytes but declare the SOAP
// response as Latin-1, so a name like "Вход" arrives as "Ð'Ñ…Ð¾Ð´". The bytes are
// intact — only the label was wrong — so recovering the byte behind each
// character and decoding it as UTF-8 gives the original back.
//
// The repair runs only on unambiguous evidence: the decoded result must
// contain a character outside Latin-1. A result that stays inside it — "Â©"
// decoding to "©", "EntrÃ©e" to "Entrée" — proves nothing, because the
// original is equally plausible as intentional text, and rewriting a name the
// camera sent correctly is worse than leaving one broken. The cost is that
// mojibake whose true text is itself Latin-1 (French, German names) stays as
// sent — still legible — while the scripts that arrive as pure noise
// (Cyrillic, Vietnamese, Greek) are the ones repaired.
public static class OnvifText
{
    public static string RepairMojibake(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        // Two decoders produce this damage in the wild: Latin-1, where every
        // byte maps straight to the same code point, and Windows-1252, which
        // differs from it only in 0x80–0x9F — the range it renders as
        // typographic characters (• – " ™ …). Handling Latin-1 alone repairs
        // barely half the fleet.
        var bytes = new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c <= 0xFF) { bytes[i] = (byte)c; continue; }
            var cp1252 = Cp1252Byte(c);
            if (cp1252 is null) return value;   // never went through either decoder
            bytes[i] = cp1252.Value;
        }

        string decoded;
        try
        {
            decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (ArgumentException)
        {
            // Not valid UTF-8 underneath — the name really was Latin-1 text.
            return value;
        }

        // Unambiguous only: a repair that never left Latin-1 cannot be told
        // apart from a name that was already right, so the original stands. A
        // replacement char means the guess was wrong outright.
        if (decoded.Contains('�')) return value;

        foreach (var c in decoded)
        {
            if (c > '\u00FF') return decoded;
        }

        return value;
    }

    // The 0x80–0x9F block of Windows-1252, the only place it differs from
    // Latin-1. Everything else in that decoder maps identically.
    private static byte? Cp1252Byte(char c) => c switch
    {
        '€' => 0x80, '‚' => 0x82, 'ƒ' => 0x83, '„' => 0x84,
        '…' => 0x85, '†' => 0x86, '‡' => 0x87, 'ˆ' => 0x88,
        '‰' => 0x89, 'Š' => 0x8A, '‹' => 0x8B, 'Œ' => 0x8C,
        'Ž' => 0x8E, '‘' => 0x91, '’' => 0x92, '“' => 0x93,
        '”' => 0x94, '•' => 0x95, '–' => 0x96, '—' => 0x97,
        '˜' => 0x98, '™' => 0x99, 'š' => 0x9A, '›' => 0x9B,
        'œ' => 0x9C, 'ž' => 0x9E, 'Ÿ' => 0x9F,
        _ => null,
    };
}
