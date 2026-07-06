using Agents.Desktop;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Tests for <see cref="LinuxWaylandDriver.TypeTextAsync"/>. The command is always the exact argv
/// <c>["type", "--", &lt;text&gt;]</c>: the <c>--</c> terminates ydotool's option parsing and the
/// model-supplied text is passed as a <b>single</b> argv element (via ArgumentList, never a shell
/// string). This is the flag-injection defense — text that looks like an option (e.g.
/// <c>--file ~/.ssh/id_ed25519</c>) is typed verbatim instead of being interpreted by ydotool.
/// Assertions are made against the recorded argv without spawning a real process.
/// </summary>
public class DesktopTypeTests
{
    private static LinuxWaylandDriver Driver(RecordingProcessRunner runner) => new(runner);

    [Fact]
    public async Task TypeTextAsync_emits_type_double_dash_then_text_as_single_arg()
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).TypeTextAsync("hello world");

        var call = runner.Calls.ShouldHaveSingleItem();
        call.File.ShouldBe("ydotool");
        call.Args.ShouldBe(new[] { "type", "--", "hello world" });
    }

    [Fact]
    public async Task TypeTextAsync_places_the_separator_at_index_1_and_text_at_index_2()
    {
        // Structural guarantee: exactly three elements, "--" present and immediately before the text.
        var runner = new RecordingProcessRunner();
        await Driver(runner).TypeTextAsync("payload");

        var call = runner.Calls.ShouldHaveSingleItem();
        call.Args.Count.ShouldBe(3);
        call.Args[0].ShouldBe("type");
        call.Args[1].ShouldBe("--");
        call.Args[2].ShouldBe("payload");
    }

    [Fact]
    public async Task TypeTextAsync_passes_flag_like_text_verbatim_after_double_dash()
    {
        // The canonical attack: model-typed text that looks like a ydotool option which would read
        // a private key file into the keystroke stream. It must arrive as ONE literal text element.
        const string text = "--file /home/x/.ssh/id_ed25519";
        var runner = new RecordingProcessRunner();
        await Driver(runner).TypeTextAsync(text);

        var call = runner.Calls.ShouldHaveSingleItem();
        call.Args.Count.ShouldBe(3);            // not split into "--file" + a path
        call.Args[1].ShouldBe("--");
        call.Args[2].ShouldBe(text);            // the whole thing, verbatim, as a single element
        call.Args.ShouldNotContain("--file");   // never surfaces as its own flag
    }

    // Assorted flag-shaped payloads: none may escape the "--" separator; each stays a single element.
    [Theory]
    [InlineData("--help")]
    [InlineData("-d")]
    [InlineData("--key-delay 0")]
    [InlineData("--file /etc/passwd")]
    [InlineData("-- --nested")]     // even leading "--" text stays as one element after the driver's own "--"
    public async Task TypeTextAsync_never_lets_flag_like_text_escape_the_double_dash(string text)
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).TypeTextAsync(text);

        var call = runner.Calls.ShouldHaveSingleItem();
        call.Args.Count.ShouldBe(3);
        call.Args[0].ShouldBe("type");
        call.Args[1].ShouldBe("--");
        call.Args[2].ShouldBe(text);
    }

    [Fact]
    public async Task TypeTextAsync_treats_a_literal_double_dash_as_text_not_a_separator()
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).TypeTextAsync("--");

        // The driver's own "--" separator sits at index 1; the user's "--" is the payload at index 2.
        var call = runner.Calls.ShouldHaveSingleItem();
        call.Args.ShouldBe(new[] { "type", "--", "--" });
    }

    [Fact]
    public async Task TypeTextAsync_passes_shell_metacharacters_and_quotes_verbatim()
    {
        // argv semantics, not a shell: quotes, subshells, redirection, and pipes are inert text.
        const string text = "He said \"hi\"; rm -rf / & echo $(whoami) `id` | cat > /tmp/x";
        var runner = new RecordingProcessRunner();
        await Driver(runner).TypeTextAsync(text);

        runner.Calls.ShouldHaveSingleItem().Args.ShouldBe(new[] { "type", "--", text });
    }

    [Fact]
    public async Task TypeTextAsync_passes_unicode_verbatim()
    {
        const string text = "café ☕ 日本語 emoji 🚀 — ünïcödé";
        var runner = new RecordingProcessRunner();
        await Driver(runner).TypeTextAsync(text);

        runner.Calls.ShouldHaveSingleItem().Args.ShouldBe(new[] { "type", "--", text });
    }

    [Fact]
    public async Task TypeTextAsync_passes_newlines_and_tabs_verbatim()
    {
        const string text = "line1\nline2\tindented\r\nwindows";
        var runner = new RecordingProcessRunner();
        await Driver(runner).TypeTextAsync(text);

        runner.Calls.ShouldHaveSingleItem().Args.ShouldBe(new[] { "type", "--", text });
    }

    [Fact]
    public async Task TypeTextAsync_preserves_leading_and_trailing_whitespace()
    {
        // No trimming: whitespace is part of the payload and must survive intact.
        const string text = "  spaced out \t tabbed  ";
        var runner = new RecordingProcessRunner();
        await Driver(runner).TypeTextAsync(text);

        runner.Calls.ShouldHaveSingleItem().Args[2].ShouldBe(text);
    }

    [Fact]
    public async Task TypeTextAsync_handles_empty_string()
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).TypeTextAsync(string.Empty);

        var call = runner.Calls.ShouldHaveSingleItem();
        call.Args.Count.ShouldBe(3);
        call.Args.ShouldBe(new[] { "type", "--", "" });
    }

    [Fact]
    public async Task TypeTextAsync_throws_ArgumentNullException_on_null_text_and_emits_nothing()
    {
        var runner = new RecordingProcessRunner();

        await Should.ThrowAsync<ArgumentNullException>(() => Driver(runner).TypeTextAsync(null!));

        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TypeTextAsync_throws_InvalidOperationException_on_nonzero_exit_and_surfaces_stderr()
    {
        var runner = new RecordingProcessRunner((_, _) => new ProcessResult(1, [], "boom"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Driver(runner).TypeTextAsync("x"));

        ex.Message.ShouldContain("boom");
    }
}
