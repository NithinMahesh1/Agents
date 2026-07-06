using Agents.Core;
using Agents.Desktop;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Tests for <see cref="LinuxWaylandDriver.DragAsync"/>: a drag is four ordered ydotool
/// invocations — position at the source, press-and-hold (down-only code), move to the
/// destination while held (the actual drag), then release (up-only code). These assert the
/// exact argv and ordering without spawning a real process.
/// </summary>
public class DesktopDragTests
{
    private static LinuxWaylandDriver Driver(RecordingProcessRunner runner) => new(runner);

    private static int ParseCode(string hex) => Convert.ToInt32(hex, 16);

    // ---- Ordering + argv --------------------------------------------------------------------

    // ydotool click byte: low bits select the button (left 0, right 1, middle 2); high bits are the
    // edges — 0x40 press, 0x80 release. A drag splits the click into its two edges so the button
    // stays held across the move: press codes 0x40/0x41/0x42, release codes 0x80/0x81/0x82.
    [Theory]
    [InlineData(MouseButton.Left,   "0x40", "0x80")]
    [InlineData(MouseButton.Right,  "0x41", "0x81")]
    [InlineData(MouseButton.Middle, "0x42", "0x82")]
    public async Task DragAsync_emits_move_press_move_release_in_order(
        MouseButton button, string pressCode, string releaseCode)
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).DragAsync(11, 22, 333, 444, button);

        runner.Calls.Count.ShouldBe(4);

        // 1) position the pointer at the source
        runner.Calls[0].File.ShouldBe("ydotool");
        runner.Calls[0].Args.ShouldBe(new[] { "mousemove", "--absolute", "-x", "11", "-y", "22" });

        // 2) press-and-hold with the down-only code
        runner.Calls[1].File.ShouldBe("ydotool");
        runner.Calls[1].Args.ShouldBe(new[] { "click", pressCode });

        // 3) move to the destination while the button is still held (the drag)
        runner.Calls[2].File.ShouldBe("ydotool");
        runner.Calls[2].Args.ShouldBe(new[] { "mousemove", "--absolute", "-x", "333", "-y", "444" });

        // 4) release with the up-only code
        runner.Calls[3].File.ShouldBe("ydotool");
        runner.Calls[3].Args.ShouldBe(new[] { "click", releaseCode });
    }

    [Fact]
    public async Task DragAsync_defaults_to_the_left_button()
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).DragAsync(1, 2, 3, 4);

        runner.Calls.Count.ShouldBe(4);
        runner.Calls[1].Args.ShouldBe(new[] { "click", "0x40" }); // left press
        runner.Calls[3].Args.ShouldBe(new[] { "click", "0x80" }); // left release
    }

    // ---- Coordinate pass-through ------------------------------------------------------------

    [Fact]
    public async Task DragAsync_passes_source_and_destination_coordinates_through_unchanged()
    {
        // Coordinates are absolute screenshot pixels (Core contract): the driver applies no DPI
        // scaling or clamping, so every value reaches ydotool verbatim on its own axis. The
        // asymmetric values would expose any from/to or x/y swap.
        var runner = new RecordingProcessRunner();
        await Driver(runner).DragAsync(1920, 0, 7, 1080);

        runner.Calls[0].Args.ShouldBe(new[] { "mousemove", "--absolute", "-x", "1920", "-y", "0" });
        runner.Calls[2].Args.ShouldBe(new[] { "mousemove", "--absolute", "-x", "7", "-y", "1080" });
    }

    // ---- Drag semantics: held across the move, not two clicks -------------------------------

    [Fact]
    public async Task DragAsync_presses_before_the_destination_move_and_releases_after_it()
    {
        // The whole point of a drag: the button goes down, THEN the pointer moves to the
        // destination, THEN it comes up. Emitting two combined 0xC0 clicks instead would click
        // twice at two spots rather than dragging.
        var runner = new RecordingProcessRunner();
        await Driver(runner).DragAsync(0, 0, 100, 100, MouseButton.Left);

        runner.Calls[1].Args[0].ShouldBe("click");     // press ...
        runner.Calls[2].Args.ShouldBe(new[] { "mousemove", "--absolute", "-x", "100", "-y", "100" }); // ... then the destination move ...
        runner.Calls[3].Args[0].ShouldBe("click");     // ... then release

        var press = ParseCode(runner.Calls[1].Args[1]);
        var release = ParseCode(runner.Calls[3].Args[1]);

        // Press carries the down edge only; release carries the up edge only. Neither is the
        // combined 0xC0 "down+up" byte that ClickAsync uses.
        (press & 0x40).ShouldBe(0x40);
        (press & 0x80).ShouldBe(0x00);
        (release & 0x80).ShouldBe(0x80);
        (release & 0x40).ShouldBe(0x00);
    }

    // ---- Cross-check: drag edges recombine into the click code ------------------------------

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    public async Task DragAsync_press_and_release_codes_or_back_to_the_click_code(MouseButton button)
    {
        // OR-ing the drag's split down/up edges must reproduce ClickAsync's combined 0xC0-family
        // byte for the same button — proving they address the same button and differ only in the
        // edge bits.
        var dragRunner = new RecordingProcessRunner();
        await Driver(dragRunner).DragAsync(0, 0, 1, 1, button);
        var press = ParseCode(dragRunner.Calls[1].Args[1]);
        var release = ParseCode(dragRunner.Calls[3].Args[1]);

        var clickRunner = new RecordingProcessRunner();
        await Driver(clickRunner).ClickAsync(button);
        var click = ParseCode(clickRunner.Calls[0].Args[1]);

        (press | release).ShouldBe(click);
    }
}
