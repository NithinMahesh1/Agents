using Agents.Desktop;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Tests for <see cref="LinuxWaylandDriver.KeyPressAsync"/> — the security-critical key-combo
/// choke point. Verifies that allowlisted combos translate to the exact <c>ydotool key</c> argv
/// (press in order, release in reverse) and, crucially, that rejected combos throw
/// <b>before</b> any process is spawned so no VT-switch, reboot, X-server "zap", or off-allowlist
/// key can ever reach ydotool. Assertions are made against the recorded argv without spawning a
/// real process. Evdev codes are read from <see cref="KeyMap"/>; rejection rules from
/// <see cref="KeyCombo"/>.
/// </summary>
public class DesktopKeyTests
{
    private static LinuxWaylandDriver Driver(RecordingProcessRunner runner) => new(runner);

    // ---- Allowed combos: exact ydotool argv -------------------------------------------------

    // Each row is a combo and the exact expected `ydotool key ...` argv. Codes come from KeyMap:
    // ctrl/control=29, alt=56, shift=42, super=125, Return/Enter=28, Tab=15, Escape=1, space=57,
    // BackSpace=14, Delete=111, Up=103, Left=105, '-'=12, a=30, z=44, digit '1'=2, digit '0'=11,
    // c=46, t=20. Press all keys down in order, then release in reverse.
    public static IEnumerable<object[]> AllowedCombos() => new[]
    {
        new object[] { "ctrl+c",     new[] { "key", "29:1", "46:1", "46:0", "29:0" } },
        new object[] { "control+c",  new[] { "key", "29:1", "46:1", "46:0", "29:0" } }, // alias collapses to 29
        new object[] { "Return",     new[] { "key", "28:1", "28:0" } },
        new object[] { "Enter",      new[] { "key", "28:1", "28:0" } },                 // alias of Return
        new object[] { "Tab",        new[] { "key", "15:1", "15:0" } },
        new object[] { "Escape",     new[] { "key", "1:1", "1:0" } },
        new object[] { "space",      new[] { "key", "57:1", "57:0" } },
        new object[] { "BackSpace",  new[] { "key", "14:1", "14:0" } },                 // allowed alone (only ctrl+alt+BackSpace is blocked)
        new object[] { "Delete",     new[] { "key", "111:1", "111:0" } },               // allowed alone (only ctrl+alt+Delete is blocked)
        new object[] { "alt+Tab",    new[] { "key", "56:1", "15:1", "15:0", "56:0" } },
        new object[] { "super+Left", new[] { "key", "125:1", "105:1", "105:0", "125:0" } },
        new object[] { "a",          new[] { "key", "30:1", "30:0" } },
        new object[] { "z",          new[] { "key", "44:1", "44:0" } },
        new object[] { "1",          new[] { "key", "2:1", "2:0" } },
        new object[] { "0",          new[] { "key", "11:1", "11:0" } },
        new object[] { "Up",         new[] { "key", "103:1", "103:0" } },
        new object[] { "ctrl+-",     new[] { "key", "29:1", "12:1", "12:0", "29:0" } }, // safe punctuation
    };

    [Theory]
    [MemberData(nameof(AllowedCombos))]
    public async Task KeyPressAsync_translates_allowed_combo_to_exact_ydotool_argv(string combo, string[] expectedArgs)
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).KeyPressAsync(combo);

        var call = runner.Calls.ShouldHaveSingleItem();
        call.File.ShouldBe("ydotool");
        call.Args.ShouldBe(expectedArgs);
    }

    [Fact]
    public async Task KeyPressAsync_presses_in_order_then_releases_in_reverse_for_multi_key_combo()
    {
        // ctrl+shift+t -> press 29,42,20 in order; release 20,42,29 in strict reverse.
        var runner = new RecordingProcessRunner();
        await Driver(runner).KeyPressAsync("ctrl+shift+t");

        var call = runner.Calls.ShouldHaveSingleItem();
        call.File.ShouldBe("ydotool");
        call.Args.ShouldBe(new[] { "key", "29:1", "42:1", "20:1", "20:0", "42:0", "29:0" });
    }

    // ---- Rejected combos: throw NotSupportedException AND never touch ydotool ----------------

    // ctrl+alt + {F1..F12, Delete/Del, BackSpace}: VT switch / reboot / X-server "zap". Blocked
    // before translation regardless of modifier order or case.
    [Theory]
    [InlineData("ctrl+alt+F1")]
    [InlineData("ctrl+alt+F2")]
    [InlineData("ctrl+alt+F7")]
    [InlineData("ctrl+alt+F12")]
    [InlineData("ctrl+alt+f6")]      // function-key match is case-insensitive
    [InlineData("alt+ctrl+F1")]      // modifier order is irrelevant to the guard
    [InlineData("ctrl+alt+Delete")]
    [InlineData("ctrl+alt+Del")]
    [InlineData("ctrl+alt+BackSpace")]
    public async Task KeyPressAsync_rejects_ctrl_alt_session_destroying_combos_and_emits_nothing(string combo)
    {
        var runner = new RecordingProcessRunner();

        await Should.ThrowAsync<NotSupportedException>(() => Driver(runner).KeyPressAsync(combo));

        runner.Calls.ShouldBeEmpty();
    }

    // Function keys are never on the allowlist — not alone, and not behind a benign modifier.
    [Theory]
    [InlineData("F1")]
    [InlineData("F5")]
    [InlineData("F10")]
    [InlineData("F12")]
    [InlineData("shift+F1")]         // still off-allowlist even without ctrl+alt
    [InlineData("ctrl+F4")]
    public async Task KeyPressAsync_rejects_function_keys_outside_the_allowlist_and_emits_nothing(string combo)
    {
        var runner = new RecordingProcessRunner();

        await Should.ThrowAsync<NotSupportedException>(() => Driver(runner).KeyPressAsync(combo));

        runner.Calls.ShouldBeEmpty();
    }

    // Any leading token that is not a recognised modifier is rejected, never passed through.
    [Theory]
    [InlineData("hyper+c")]
    [InlineData("fn+a")]
    [InlineData("altgr+e")]          // altgr is deliberately NOT a recognised modifier
    [InlineData("mod4+Tab")]
    [InlineData("hyper+ctrl+c")]     // an unknown modifier anywhere in the chain is rejected
    public async Task KeyPressAsync_rejects_unknown_modifiers_and_emits_nothing(string combo)
    {
        var runner = new RecordingProcessRunner();

        await Should.ThrowAsync<NotSupportedException>(() => Driver(runner).KeyPressAsync(combo));

        runner.Calls.ShouldBeEmpty();
    }

    // A well-formed combo whose final key is outside the allowlist (Print/SysRq/Insert/etc.).
    [Theory]
    [InlineData("Insert")]
    [InlineData("Print")]
    [InlineData("SysRq")]
    [InlineData("Menu")]
    [InlineData("CapsLock")]
    [InlineData("NumLock")]
    [InlineData("ctrl+Print")]       // off-allowlist final key even without ctrl+alt
    [InlineData("ctrl+shift+PrtSc")]
    public async Task KeyPressAsync_rejects_off_allowlist_final_keys_and_emits_nothing(string combo)
    {
        var runner = new RecordingProcessRunner();

        await Should.ThrowAsync<NotSupportedException>(() => Driver(runner).KeyPressAsync(combo));

        runner.Calls.ShouldBeEmpty();
    }

    // Empty / malformed input is rejected with ArgumentException before any emission (a distinct
    // category from the NotSupportedException "well-formed but disallowed" path above).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("+++")]
    public async Task KeyPressAsync_throws_ArgumentException_on_empty_or_malformed_input_and_emits_nothing(string combo)
    {
        var runner = new RecordingProcessRunner();

        await Should.ThrowAsync<ArgumentException>(() => Driver(runner).KeyPressAsync(combo));

        runner.Calls.ShouldBeEmpty();
    }

    // ---- Process failure surfaces as InvalidOperationException ------------------------------

    [Fact]
    public async Task KeyPressAsync_throws_InvalidOperationException_on_nonzero_exit_and_surfaces_stderr()
    {
        var runner = new RecordingProcessRunner((_, _) => new ProcessResult(1, [], "boom"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Driver(runner).KeyPressAsync("ctrl+c"));

        ex.Message.ShouldContain("boom");
    }
}
