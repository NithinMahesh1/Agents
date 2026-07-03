namespace Agents.Core;

/// <summary>The "hands and eyes": captures the screen and injects real input on a platform.</summary>
public interface IDesktopDriver
{
    /// <summary>Short platform id, e.g. "linux-wayland", "windows", "macos".</summary>
    string Platform { get; }

    ScreenInfo GetScreenInfo();
    Task<ScreenCapture> CaptureAsync(CancellationToken ct = default);
    Task MoveMouseAsync(int x, int y, CancellationToken ct = default);
    Task ClickAsync(MouseButton button = MouseButton.Left, CancellationToken ct = default);
    Task DoubleClickAsync(MouseButton button = MouseButton.Left, CancellationToken ct = default);

    /// <summary>Press at (fromX,fromY), move to (toX,toY), release — drag / text-selection.</summary>
    Task DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, CancellationToken ct = default);

    Task TypeTextAsync(string text, CancellationToken ct = default);

    /// <summary>Press a key combo such as "Return", "ctrl+c", "alt+Tab".</summary>
    Task KeyPressAsync(string keyCombo, CancellationToken ct = default);

    Task ScrollAsync(int dx, int dy, CancellationToken ct = default);
}

/// <summary>The "brain": looks at the context and returns the next action(s).</summary>
public interface IModelProvider
{
    /// <summary>Display name, e.g. "ollama:qwen2.5-vl", "claude", "openai".</summary>
    string Name { get; }

    Task<IReadOnlyList<AgentAction>> DecideAsync(AgentContext context, CancellationToken ct = default);
}

/// <summary>Optional Set-of-Marks grounding source (e.g. the AT-SPI accessibility tree).</summary>
public interface IGroundingProvider
{
    Task<IReadOnlyList<UiElement>> GetElementsAsync(CancellationToken ct = default);
}
