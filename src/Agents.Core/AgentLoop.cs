namespace Agents.Core;

/// <summary>Tunables and the safety gate for an <see cref="AgentLoop"/> run.</summary>
public sealed record AgentLoopOptions
{
    /// <summary>Hard cap on steps before the loop aborts.</summary>
    public int MaxSteps { get; init; } = 25;

    /// <summary>When true, actions are logged but never executed.</summary>
    public bool DryRun { get; init; }

    /// <summary>Called before each action; return false to veto it (kill-switch / confirmation).</summary>
    public Func<AgentAction, bool>? ConfirmAction { get; init; }

    /// <summary>Pause after each action so the UI can settle before the next screenshot.</summary>
    public int StepDelayMs { get; init; } = 400;

    /// <summary>Optional sink for progress/log lines (keeps Core dependency-free).</summary>
    public Action<string>? Log { get; init; }
}

public enum AgentRunOutcome
{
    Completed,
    Failed,
    MaxStepsReached,
    Aborted,
}

public sealed record AgentRunResult(AgentRunOutcome Outcome, string? Message, IReadOnlyList<AgentStep> History);

/// <summary>
/// Orchestrates the computer-use loop: capture → (ground) → decide → execute → repeat,
/// behind a safety gate, until the model signals Done/Fail or limits are hit.
/// </summary>
public sealed class AgentLoop
{
    private readonly IDesktopDriver _driver;
    private readonly IModelProvider _model;
    private readonly IGroundingProvider? _grounding;
    private readonly AgentLoopOptions _options;

    public AgentLoop(
        IDesktopDriver driver,
        IModelProvider model,
        IGroundingProvider? grounding = null,
        AgentLoopOptions? options = null)
    {
        _driver = driver;
        _model = model;
        _grounding = grounding;
        _options = options ?? new AgentLoopOptions();
    }

    public async Task<AgentRunResult> RunAsync(string goal, CancellationToken ct = default)
    {
        var history = new List<AgentStep>();

        for (var step = 0; step < _options.MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            var screen = await _driver.CaptureAsync(ct);
            IReadOnlyList<UiElement> elements = _grounding is null
                ? []
                : await _grounding.GetElementsAsync(ct);

            var context = new AgentContext
            {
                Goal = goal,
                Screen = screen,
                Elements = elements,
                History = history,
            };

            var actions = await _model.DecideAsync(context, ct);

            foreach (var action in actions)
            {
                if (action.Type == AgentActionType.Done)
                    return new AgentRunResult(AgentRunOutcome.Completed, action.Message, history);
                if (action.Type == AgentActionType.Fail)
                    return new AgentRunResult(AgentRunOutcome.Failed, action.Message, history);

                if (_options.ConfirmAction is { } confirm && !confirm(action))
                {
                    _options.Log?.Invoke($"Action vetoed by safety gate: {action.Type}");
                    return new AgentRunResult(AgentRunOutcome.Aborted, "Vetoed by safety gate", history);
                }

                var result = await ExecuteAsync(action, elements, ct);
                history.Add(new AgentStep(action, result));
                _options.Log?.Invoke($"step {history.Count}: {action.Type} -> {result}");

                if (_options.StepDelayMs > 0)
                    await Task.Delay(_options.StepDelayMs, ct);
            }
        }

        return new AgentRunResult(AgentRunOutcome.MaxStepsReached, $"Hit MaxSteps={_options.MaxSteps}", history);
    }

    private async Task<string> ExecuteAsync(AgentAction a, IReadOnlyList<UiElement> elements, CancellationToken ct)
    {
        var (x, y) = ResolveTarget(a, elements);

        if (_options.DryRun)
        {
            _options.Log?.Invoke($"[dry-run] {a.Type} x={x} y={y} text={a.Text} key={a.Key}");
            return "dry-run";
        }

        switch (a.Type)
        {
            case AgentActionType.Move:
                if (x is null || y is null) return "move: missing coordinates";
                await _driver.MoveMouseAsync(x.Value, y.Value, ct);
                return "ok";

            case AgentActionType.Click:
                if (x is not null && y is not null) await _driver.MoveMouseAsync(x.Value, y.Value, ct);
                await _driver.ClickAsync(a.Button, ct);
                return "ok";

            case AgentActionType.DoubleClick:
                if (x is not null && y is not null) await _driver.MoveMouseAsync(x.Value, y.Value, ct);
                await _driver.DoubleClickAsync(a.Button, ct);
                return "ok";

            case AgentActionType.Type:
                await _driver.TypeTextAsync(a.Text ?? string.Empty, ct);
                return "ok";

            case AgentActionType.Key:
                await _driver.KeyPressAsync(a.Key ?? string.Empty, ct);
                return "ok";

            case AgentActionType.Scroll:
                await _driver.ScrollAsync(a.ScrollDx ?? 0, a.ScrollDy ?? 0, ct);
                return "ok";

            case AgentActionType.Wait:
                await Task.Delay(a.WaitMs ?? 500, ct);
                return "ok";

            case AgentActionType.Screenshot:
                return "ok"; // a fresh capture happens at the top of the next loop iteration

            default:
                return $"unhandled: {a.Type}";
        }
    }

    /// <summary>Resolve a Set-of-Marks element index to coordinates; else use raw X/Y.</summary>
    private static (int? X, int? Y) ResolveTarget(AgentAction a, IReadOnlyList<UiElement> elements)
    {
        if (a.Element is { } idx)
        {
            foreach (var el in elements)
            {
                if (el.Index == idx)
                    return el.Center;
            }
        }

        return (a.X, a.Y);
    }
}
