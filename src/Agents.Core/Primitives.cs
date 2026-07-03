namespace Agents.Core;

/// <summary>The kind of action a model wants the desktop driver to perform.</summary>
public enum AgentActionType
{
    Move,
    Click,
    DoubleClick,
    Drag,
    Type,
    Key,
    Scroll,
    Wait,
    Screenshot,
    Done,
    Fail,
}

/// <summary>Mouse button for click / drag actions.</summary>
public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// A single normalized action emitted by a model (as JSON) and executed by <see cref="AgentLoop"/>.
///
/// <para><b>Coordinate contract:</b> <see cref="X"/>/<see cref="Y"/> and <see cref="ToX"/>/<see cref="ToY"/>
/// are in <i>screenshot-pixel</i> space — origin top-left, the exact pixels the model sees in the
/// capture. Drivers translate to device coordinates (honouring <see cref="ScreenInfo.Scale"/>). The MVP
/// assumes a single monitor. Prefer <see cref="Element"/> (a Set-of-Marks index) when one matches; the
/// loop resolves it to pixel coordinates. Fields not relevant to a given <see cref="Type"/> stay null.</para>
/// </summary>
public sealed record AgentAction
{
    public required AgentActionType Type { get; init; }

    /// <summary>Target X in screenshot pixels (move/click/double-click, and drag START). Ignored if <see cref="Element"/> is set.</summary>
    public int? X { get; init; }
    public int? Y { get; init; }

    /// <summary>Drag DESTINATION X in screenshot pixels (for <see cref="AgentActionType.Drag"/>). Ignored if <see cref="ToElement"/> is set.</summary>
    public int? ToX { get; init; }
    public int? ToY { get; init; }

    public MouseButton Button { get; init; } = MouseButton.Left;

    /// <summary>Text to type (for <see cref="AgentActionType.Type"/>).</summary>
    public string? Text { get; init; }

    /// <summary>Key combo such as "Return", "ctrl+c", "alt+Tab" (for <see cref="AgentActionType.Key"/>).</summary>
    public string? Key { get; init; }

    /// <summary>Scroll amount in detents: positive <see cref="ScrollDy"/> scrolls DOWN, positive <see cref="ScrollDx"/> scrolls RIGHT.</summary>
    public int? ScrollDx { get; init; }
    public int? ScrollDy { get; init; }

    /// <summary>Set-of-Marks element index for the target; resolved to <see cref="X"/>/<see cref="Y"/> by the loop.</summary>
    public int? Element { get; init; }

    /// <summary>Set-of-Marks element index for a drag destination; resolved to <see cref="ToX"/>/<see cref="ToY"/>.</summary>
    public int? ToElement { get; init; }

    public int? WaitMs { get; init; }

    /// <summary>Final answer/reason on Done/Fail, or rationale for the step.</summary>
    public string? Message { get; init; }
}

/// <summary>Physical screen geometry. <see cref="Scale"/> is the HiDPI/fractional factor (1.0 = 100%).</summary>
public sealed record ScreenInfo(int Width, int Height, double Scale = 1.0);

/// <summary>A captured frame: PNG bytes plus the geometry they were taken at.</summary>
public sealed record ScreenCapture(byte[] PngBytes, ScreenInfo Screen);

/// <summary>An on-screen UI element from the accessibility tree (Set-of-Marks grounding).</summary>
public sealed record UiElement(int Index, string Role, string Name, int X, int Y, int Width, int Height)
{
    /// <summary>Centre point (screenshot pixels) — where a click on this element lands.</summary>
    public (int X, int Y) Center => (X + (Width / 2), Y + (Height / 2));
}

/// <summary>One executed (or attempted) step in the loop's history.</summary>
public sealed record AgentStep(AgentAction Action, bool Ok, string? Detail = null);

/// <summary>Everything a model needs to choose the next action.</summary>
public sealed record AgentContext
{
    public required string Goal { get; init; }
    public required ScreenCapture Screen { get; init; }
    public IReadOnlyList<UiElement> Elements { get; init; } = [];
    public IReadOnlyList<AgentStep> History { get; init; } = [];

    /// <summary>
    /// One-off corrective notice for the model (e.g. "your last output was not valid JSON") — set by
    /// the loop after an unparseable/empty response so the model can self-correct. Null most turns.
    /// </summary>
    public string? Notice { get; init; }
}
