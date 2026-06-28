namespace Agents.Core;

/// <summary>The kind of action a model wants the desktop driver to perform.</summary>
public enum AgentActionType
{
    Move,
    Click,
    DoubleClick,
    Type,
    Key,
    Scroll,
    Wait,
    Screenshot,
    Done,
    Fail,
}

/// <summary>Mouse button for click actions.</summary>
public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// A single normalized action. Models emit these as JSON; <see cref="AgentLoop"/> maps them
/// onto <see cref="IDesktopDriver"/> calls. Fields not relevant to a given type stay null.
/// </summary>
public sealed record AgentAction
{
    public required AgentActionType Type { get; init; }

    /// <summary>Absolute pixel coordinates (for move/click). Ignored when <see cref="Element"/> is set.</summary>
    public int? X { get; init; }
    public int? Y { get; init; }

    public MouseButton Button { get; init; } = MouseButton.Left;

    /// <summary>Text to type (for <see cref="AgentActionType.Type"/>).</summary>
    public string? Text { get; init; }

    /// <summary>Key combo such as "Return", "ctrl+c", "alt+Tab" (for <see cref="AgentActionType.Key"/>).</summary>
    public string? Key { get; init; }

    public int? ScrollDx { get; init; }
    public int? ScrollDy { get; init; }

    /// <summary>Set-of-Marks element index the model chose; resolved to coordinates by the loop.</summary>
    public int? Element { get; init; }

    public int? WaitMs { get; init; }

    /// <summary>Final answer/reason on Done/Fail, or rationale for the step.</summary>
    public string? Message { get; init; }
}

/// <summary>Physical screen geometry. <see cref="Scale"/> is the HiDPI factor (1.0 = 100%).</summary>
public sealed record ScreenInfo(int Width, int Height, double Scale = 1.0);

/// <summary>A captured frame: PNG bytes plus the geometry they were taken at.</summary>
public sealed record ScreenCapture(byte[] PngBytes, ScreenInfo Screen);

/// <summary>An on-screen UI element from the accessibility tree (Set-of-Marks grounding).</summary>
public sealed record UiElement(int Index, string Role, string Name, int X, int Y, int Width, int Height)
{
    /// <summary>Centre point — where a click on this element lands.</summary>
    public (int X, int Y) Center => (X + (Width / 2), Y + (Height / 2));
}

/// <summary>One executed step in the loop's history.</summary>
public sealed record AgentStep(AgentAction Action, string? Result = null);

/// <summary>Everything a model needs to choose the next action.</summary>
public sealed record AgentContext
{
    public required string Goal { get; init; }
    public required ScreenCapture Screen { get; init; }
    public IReadOnlyList<UiElement> Elements { get; init; } = [];
    public IReadOnlyList<AgentStep> History { get; init; } = [];
}
