using Agents.Core;

namespace Agents.Desktop;

/// <summary>
/// Windows <see cref="IDesktopDriver"/> skeleton. Every member currently throws
/// <see cref="PlatformNotSupportedException"/>; the real implementation is not yet written.
/// </summary>
/// <remarks>
/// TODO: implement input via the Win32 <c>SendInput</c> P/Invoke (mouse move/click/scroll with
/// <c>MOUSEINPUT</c>, keystrokes with <c>KEYBDINPUT</c> and scan codes), and capture via
/// <c>Graphics.CopyFromScreen</c> / <c>BitBlt</c> against the virtual screen, encoding the
/// result to PNG. Populate <see cref="ScreenInfo"/> from <c>GetSystemMetrics</c> /
/// per-monitor DPI awareness.
/// </remarks>
public sealed class WindowsDriver : IDesktopDriver
{
    private const string NotImplementedMessage =
        "WindowsDriver is a skeleton; the Win32 SendInput/BitBlt implementation is not yet written.";

    /// <inheritdoc />
    public string Platform => "windows";

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
