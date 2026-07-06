using Agents.Core;
using Agents.Desktop;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Tests for the mouse-input operations in <see cref="LinuxWaylandDriver"/>: verifies that
/// <see cref="LinuxWaylandDriver.MoveMouseAsync"/>, <see cref="LinuxWaylandDriver.ClickAsync"/>,
/// <see cref="LinuxWaylandDriver.DoubleClickAsync"/>, and <see cref="LinuxWaylandDriver.ScrollAsync"/>
/// emit the exact ydotool argv without spawning a real process.
/// </summary>
public class DesktopMouseTests
{
    private static LinuxWaylandDriver Driver(RecordingProcessRunner runner) => new(runner);

    // ---- MoveMouseAsync ---------------------------------------------------------------------

    [Fact]
    public async Task MoveMouseAsync_emits_mousemove_absolute_with_exact_coordinates()
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).MoveMouseAsync(123, 456);

        var call = runner.Calls.ShouldHaveSingleItem();
        call.File.ShouldBe("ydotool");
        call.Args.ShouldBe(new[] { "mousemove", "--absolute", "-x", "123", "-y", "456" });
    }

    [Fact]
    public async Task MoveMouseAsync_passes_origin_coordinates_straight_through()
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).MoveMouseAsync(0, 0);

        runner.Calls[0].Args.ShouldBe(new[] { "mousemove", "--absolute", "-x", "0", "-y", "0" });
    }

    [Fact]
    public async Task MoveMouseAsync_throws_InvalidOperationException_on_nonzero_exit_and_surfaces_stderr()
    {
        var runner = new RecordingProcessRunner((_, _) => new ProcessResult(1, [], "boom"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Driver(runner).MoveMouseAsync(10, 20));

        ex.Message.ShouldContain("boom");
    }

    // ---- ClickAsync -------------------------------------------------------------------------

    // ydotool click codes: low bits = button index (left 0, right 1, middle 2);
    // high bits = edges (0x40 down | 0x80 up = 0xC0 down+up combined).
    [Theory]
    [InlineData(MouseButton.Left,   "0xC0")]
    [InlineData(MouseButton.Right,  "0xC1")]
    [InlineData(MouseButton.Middle, "0xC2")]
    public async Task ClickAsync_emits_click_with_correct_button_code(MouseButton button, string expectedCode)
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).ClickAsync(button);

        var call = runner.Calls.ShouldHaveSingleItem();
        call.File.ShouldBe("ydotool");
        call.Args.ShouldBe(new[] { "click", expectedCode });
    }

    [Fact]
    public async Task ClickAsync_throws_InvalidOperationException_on_nonzero_exit_and_surfaces_stderr()
    {
        var runner = new RecordingProcessRunner((_, _) => new ProcessResult(1, [], "boom"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Driver(runner).ClickAsync());

        ex.Message.ShouldContain("boom");
    }

    // ---- DoubleClickAsync -------------------------------------------------------------------

    // DoubleClick emits a single ydotool invocation with the click code repeated twice so both
    // clicks fall within the compositor's double-click interval.
    [Theory]
    [InlineData(MouseButton.Left,   "0xC0")]
    [InlineData(MouseButton.Right,  "0xC1")]
    [InlineData(MouseButton.Middle, "0xC2")]
    public async Task DoubleClickAsync_emits_click_with_button_code_repeated_twice(MouseButton button, string expectedCode)
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).DoubleClickAsync(button);

        var call = runner.Calls.ShouldHaveSingleItem();
        call.File.ShouldBe("ydotool");
        call.Args.ShouldBe(new[] { "click", expectedCode, expectedCode });
    }

    [Fact]
    public async Task DoubleClickAsync_throws_InvalidOperationException_on_nonzero_exit_and_surfaces_stderr()
    {
        var runner = new RecordingProcessRunner((_, _) => new ProcessResult(1, [], "boom"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Driver(runner).DoubleClickAsync());

        ex.Message.ShouldContain("boom");
    }

    // ---- ScrollAsync -----------------------------------------------------------------------

    // Core sign convention: positive dy = scroll DOWN, positive dx = scroll RIGHT.
    // ydotool REL_WHEEL sign is inverted (positive = UP), so the driver negates dy.
    // REL_HWHEEL already matches Core (positive = right), so dx passes straight through.

    [Fact]
    public async Task ScrollAsync_negates_dy_for_REL_WHEEL_sign_inversion()
    {
        // positive dy (scroll down in Core) → negative y sent to ydotool
        var runner = new RecordingProcessRunner();
        await Driver(runner).ScrollAsync(0, 3);

        var call = runner.Calls.ShouldHaveSingleItem();
        call.File.ShouldBe("ydotool");
        call.Args.ShouldBe(new[] { "mousemove", "--wheel", "-x", "0", "-y", "-3" });
    }

    [Fact]
    public async Task ScrollAsync_negative_dy_produces_positive_y_wheel_value()
    {
        // negative dy (scroll up in Core) → negated → positive y sent to ydotool
        var runner = new RecordingProcessRunner();
        await Driver(runner).ScrollAsync(0, -2);

        runner.Calls[0].Args.ShouldBe(new[] { "mousemove", "--wheel", "-x", "0", "-y", "2" });
    }

    [Fact]
    public async Task ScrollAsync_passes_dx_straight_through_without_sign_change()
    {
        var runner = new RecordingProcessRunner();
        await Driver(runner).ScrollAsync(5, 0);

        runner.Calls[0].Args.ShouldBe(new[] { "mousemove", "--wheel", "-x", "5", "-y", "0" });
    }

    [Fact]
    public async Task ScrollAsync_throws_InvalidOperationException_on_nonzero_exit_and_surfaces_stderr()
    {
        var runner = new RecordingProcessRunner((_, _) => new ProcessResult(1, [], "boom"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Driver(runner).ScrollAsync(1, 1));

        ex.Message.ShouldContain("boom");
    }
}
