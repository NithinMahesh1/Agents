using Agents.Core;

namespace Agents.Desktop;

/// <summary>
/// macOS <see cref="IDesktopDriver"/> skeleton. Every member currently throws
/// <see cref="PlatformNotSupportedException"/>; the real implementation is not yet written.
/// </summary>
/// <remarks>
/// TODO: implement input via <c>CGEventPost</c> (mouse/keyboard events through the CoreGraphics
/// event tap) and capture via <c>CGWindowListCreateImage</c> (or <c>ScreenCaptureKit</c> on newer
/// macOS), encoding the result to PNG. Both require user-granted Accessibility and
/// Screen-Recording permissions, which must be surfaced clearly at runtime.
/// </remarks>
public sealed class MacDriver : IDesktopDriver
{
    private const string NotImplementedMessage =
        "MacDriver is a skeleton; the CGEventPost/CGWindowListCreateImage implementation is not yet written.";

    /// <inheritdoc />
    public string Platform => "macos";

    /// <inheritdoc />
    public ScreenInfo GetScreenInfo() => throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    public Task<ScreenCapture> CaptureAsync(CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    public Task MoveMouseAsync(int x, int y, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    public Task ClickAsync(MouseButton button = MouseButton.Left, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    public Task DoubleClickAsync(MouseButton button = MouseButton.Left, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    public Task DragAsync(
        int fromX,
        int fromY,
        int toX,
        int toY,
        MouseButton button = MouseButton.Left,
        CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    public Task TypeTextAsync(string text, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    public Task KeyPressAsync(string keyCombo, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    public Task ScrollAsync(int dx, int dy, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);
}
