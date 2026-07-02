namespace Agents.Desktop;

/// <summary>
/// Maps human-readable key names to Linux evdev key codes (from
/// <c>linux/input-event-codes.h</c>) as consumed by <c>ydotool key</c>. Lookups are
/// case-insensitive and cover a common subset: modifiers, a handful of named keys, the
/// ASCII letters a–z, and the digits 0–9.
/// </summary>
internal static class KeyMap
{
    private static readonly IReadOnlyDictionary<string, int> Codes = Build();

    /// <summary>Attempts to resolve <paramref name="keyName"/> to its evdev key code.</summary>
    public static bool TryGetCode(string keyName, out int code) => Codes.TryGetValue(keyName, out code);

    /// <summary>
    /// Resolves <paramref name="keyName"/> to its evdev key code, throwing
    /// <see cref="NotSupportedException"/> for names outside the supported subset.
    /// </summary>
    public static int GetCode(string keyName)
    {
        if (Codes.TryGetValue(keyName, out var code))
        {
            return code;
        }

        throw new NotSupportedException(
            $"Key '{keyName}' is not in the ydotool key map. Supported: modifiers " +
            "(ctrl/control, alt, shift, super/meta/win/cmd), named keys " +
            "(Return/Enter, Tab, Escape/Esc, space, BackSpace), letters a–z, and digits 0–9.");
    }

    private static Dictionary<string, int> Build()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // Named keys.
            ["Return"] = 28,
            ["Enter"] = 28,
            ["Tab"] = 15,
            ["Escape"] = 1,
            ["Esc"] = 1,
            ["space"] = 57,
            ["BackSpace"] = 14,

            // Modifiers (aliases collapse onto the left-hand variant).
            ["ctrl"] = 29,
            ["control"] = 29,
            ["leftctrl"] = 29,
            ["alt"] = 56,
            ["leftalt"] = 56,
            ["shift"] = 42,
            ["leftshift"] = 42,
            ["super"] = 125,
            ["meta"] = 125,
            ["win"] = 125,
            ["cmd"] = 125,
            ["leftmeta"] = 125,
        };

        // Letters a–z, indexed by (letter - 'a').
        int[] letters =
        {
            30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50,
            49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44,
        };
        for (var i = 0; i < letters.Length; i++)
        {
            map[((char)('a' + i)).ToString()] = letters[i];
        }

        // Digits 0–9, indexed by (digit - '0').
        int[] digits = { 11, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        for (var i = 0; i < digits.Length; i++)
        {
            map[((char)('0' + i)).ToString()] = digits[i];
        }

        return map;
    }
}
