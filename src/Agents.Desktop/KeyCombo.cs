using System.Text.RegularExpressions;

namespace Agents.Desktop;

/// <summary>
/// Security choke point for model-supplied key combos. The <c>Key</c> string is model-controlled,
/// so every combo must pass this validator before a single keystroke reaches <c>ydotool</c>. It
/// enforces a strict <c>[modifier+]*key</c> grammar whose final key is drawn from an explicit
/// allowlist, and hard-rejects combos that can strand or tear down the session:
/// <list type="bullet">
///   <item><description><c>ctrl+alt+F1..F12</c> — switches virtual terminals, stranding the session at a text console.</description></item>
///   <item><description><c>ctrl+alt+Delete</c> — reboot / session teardown.</description></item>
///   <item><description><c>ctrl+alt+BackSpace</c> — the X "zap" that kills the display server.</description></item>
///   <item><description><c>alt+SysRq</c> / Print — reaches the kernel SysRq handler (excluded from the allowlist).</description></item>
/// </list>
/// Rejected combos throw <see cref="NotSupportedException"/> and are <b>never emitted</b>, so no
/// model output can VT-switch, kill the display server, or trigger SysRq. Anything that passes is
/// guaranteed to be translatable by <see cref="KeyMap"/> (the allowlist is kept in sync with it).
/// </summary>
internal static partial class KeyCombo
{
    // Modifier tokens that may prefix the final key. Aliases are accepted here and collapsed onto
    // evdev codes by KeyMap; note the set deliberately contains no "altgr" (a dead key on many
    // layouts) and no way to name a raw function/lock key as a modifier.
    private static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ctrl", "control", "alt", "shift", "super", "meta", "win", "cmd",
    };

    // Allowlist of final keys (letters a–z and digits 0–9 are matched separately, and punctuation
    // via AllowedPunctuation). Function keys (F1..F12), Print/SysRq, and other keys that can reach a
    // VT switch or the kernel are intentionally absent — they must never be emittable. Every entry
    // here must resolve in KeyMap so a validated combo never trips KeyMap's own NotSupportedException.
    private static readonly HashSet<string> AllowedNamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Return", "Enter", "Tab", "Escape", "Esc", "space", "BackSpace", "Delete", "Del",
        "Home", "End", "PageUp", "PageDown",
        "Up", "Down", "Left", "Right",
    };

    // Safe single-character punctuation usable as a combo key (e.g. ctrl+- to zoom out, ctrl+, for
    // settings). Each is an unshifted evdev key mapped by KeyMap; none can reach a privileged path.
    private static readonly HashSet<string> AllowedPunctuation = new(StringComparer.Ordinal)
    {
        "-", "=", "[", "]", ";", "'", "`", "\\", ",", ".", "/",
    };

    /// <summary>
    /// Validates <paramref name="keyCombo"/> and returns its parts (modifiers first, final key
    /// last), trimmed and in order, ready for <see cref="KeyMap"/> translation. Nothing is emitted
    /// here — callers translate and inject only after a successful return.
    /// </summary>
    /// <exception cref="ArgumentException">The combo is empty / malformed input.</exception>
    /// <exception cref="NotSupportedException">The combo is well-formed but disallowed.</exception>
    public static IReadOnlyList<string> Validate(string keyCombo)
    {
        if (string.IsNullOrWhiteSpace(keyCombo))
        {
            throw new ArgumentException("Key combo must not be empty.", nameof(keyCombo));
        }

        var parts = keyCombo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException($"Key combo '{keyCombo}' contained no keys.", nameof(keyCombo));
        }

        var finalKey = parts[^1];
        var hasCtrl = false;
        var hasAlt = false;

        // Every part before the last must be a recognised modifier; unknown leading tokens are
        // rejected rather than silently passed through to ydotool.
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var mod = parts[i];
            if (!Modifiers.Contains(mod))
            {
                throw new NotSupportedException(
                    $"Key combo '{keyCombo}' is rejected: '{mod}' is not a recognised modifier " +
                    "(ctrl, control, alt, shift, super, meta, win, cmd).");
            }

            hasCtrl |= mod.Equals("ctrl", StringComparison.OrdinalIgnoreCase)
                    || mod.Equals("control", StringComparison.OrdinalIgnoreCase);
            hasAlt |= mod.Equals("alt", StringComparison.OrdinalIgnoreCase);
        }

        // Hard block, checked BEFORE the allowlist so it holds even if a function key is ever added
        // to the allowlist: with ctrl+alt held, a function key switches VTs, Delete reboots, and
        // BackSpace zaps the X server. None of these may ever be emitted.
        if (hasCtrl && hasAlt && IsCtrlAltDangerousKey(finalKey))
        {
            throw new NotSupportedException(
                $"Key combo '{keyCombo}' is rejected: ctrl+alt+{finalKey} can switch virtual " +
                "terminals, reboot, or kill the display server and is never permitted.");
        }

        if (!IsAllowedFinalKey(finalKey))
        {
            throw new NotSupportedException(
                $"Key combo '{keyCombo}' is rejected: '{finalKey}' is not an allowed key. Allowed " +
                "final keys are letters, digits, Return/Enter, Tab, Escape, space, BackSpace, " +
                "Delete, arrows, Home/End/PageUp/PageDown, and safe punctuation.");
        }

        return parts;
    }

    private static bool IsAllowedFinalKey(string key) =>
        (key.Length == 1 && (char.IsAsciiLetterOrDigit(key[0]) || AllowedPunctuation.Contains(key)))
        || AllowedNamedKeys.Contains(key);

    // Keys that turn a ctrl+alt chord into a VT switch (function keys), a reboot (Delete), or an
    // X-server "zap" (BackSpace).
    private static bool IsCtrlAltDangerousKey(string key) =>
        IsFunctionKey(key)
        || key.Equals("Delete", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Del", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BackSpace", StringComparison.OrdinalIgnoreCase);

    private static bool IsFunctionKey(string key) => FunctionKeyRegex().IsMatch(key);

    // F1 through F12 (case-insensitive), e.g. "F1", "f12".
    [GeneratedRegex(@"^[fF](?:[1-9]|1[0-2])$")]
    private static partial Regex FunctionKeyRegex();
}
