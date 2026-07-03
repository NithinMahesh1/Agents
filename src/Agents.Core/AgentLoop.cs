namespace Agents.Core;

/// <summary>Outcome of a confirmation prompt for a single (resolved) action.</summary>
public enum ConfirmDecision
{
    /// <summary>Execute the action.</summary>
    Allow,

    /// <summary>Skip this action; the model re-plans on the next turn. Does NOT end the run.</summary>
    Deny,

    /// <summary>Stop the whole run immediately — the kill decision.</summary>
    Abort,
}

/// <summary>A resolved action presented to the confirmation gate — coordinates are already resolved.</summary>
public sealed record ActionConfirmation(
    AgentActionType Type,
    int? X,
    int? Y,
    int? ToX,
    int? ToY,
    MouseButton Button,
    string? Text,
    string? Key,
    string Description);

/// <summary>Tunables and the safety gate for an <see cref="AgentLoop"/> run.</summary>
public sealed record AgentLoopOptions
{
    /// <summary>Max model turns (capture → decide) before aborting.</summary>
    public int MaxSteps { get; init; } = 25;

    /// <summary>Max total executed actions before aborting (independent of turns).</summary>
    public int MaxActions { get; init; } = 60;

    /// <summary>Consecutive empty / unparseable model responses tolerated before giving up.</summary>
    public int MaxConsecutiveEmpty { get; init; } = 3;

    /// <summary>When true, actions are logged but never executed.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Async confirmation gate, called once per action AFTER its target is resolved (so it can show the
    /// real coordinates / element). Return Allow / Deny (skip &amp; re-plan) / Abort (kill). Null means
    /// auto-allow everything — use only for trusted or dry runs.
    /// </summary>
    public Func<ActionConfirmation, ValueTask<ConfirmDecision>>? Confirm { get; init; }

    /// <summary>Pause after each action so the UI settles before the next screenshot. Also the human-reaction window — do not lower it for real runs.</summary>
    public int StepDelayMs { get; init; } = 400;

    /// <summary>Optional sink for progress/log lines (keeps Core dependency-free).</summary>
    public Action<string>? Log { get; init; }
}

public enum AgentRunOutcome
{
    Completed,
    Failed,
    MaxStepsReached,
    MaxActionsReached,
    Aborted,
}

public sealed record AgentRunResult(AgentRunOutcome Outcome, string? Message, IReadOnlyList<AgentStep> History);

/// <summary>
/// Orchestrates the computer-use loop: capture → (ground) → decide → resolve → confirm → execute,
/// behind an async safety gate, until the model signals Done/Fail or a limit is hit.
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
        string? notice = null;
        var consecutiveEmpty = 0;
        var executed = 0;

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
                Notice = notice,
            };

            var actions = await _model.DecideAsync(context, ct);

            // Parse/empty feedback: tell the model it produced nothing usable, then retry — up to a cap.
            if (actions.Count == 0)
            {
                consecutiveEmpty++;
                _options.Log?.Invoke($"no valid action ({consecutiveEmpty}/{_options.MaxConsecutiveEmpty})");
                if (consecutiveEmpty >= _options.MaxConsecutiveEmpty)
                    return new AgentRunResult(AgentRunOutcome.Failed, "Model produced no valid actions repeatedly", history);
                notice = "Your previous response could not be parsed into an action. Respond with ONLY the JSON array described — exactly one action.";
                continue;
            }

            consecutiveEmpty = 0;
            notice = null;

            foreach (var action in actions)
            {
                if (action.Type == AgentActionType.Done)
                    return new AgentRunResult(AgentRunOutcome.Completed, action.Message, history);
                if (action.Type == AgentActionType.Fail)
                    return new AgentRunResult(AgentRunOutcome.Failed, action.Message, history);

                // Resolve targets FIRST so the gate and executor see real coordinates.
                var target = ResolveTarget(action.Element, action.X, action.Y, elements);
                var dest = ResolveTarget(action.ToElement, action.ToX, action.ToY, elements);

                // Never blind-click: an action that needs a target but can't resolve one fails the batch.
                if (NeedsTarget(action.Type) && target is null)
                {
                    history.Add(new AgentStep(action, false, "unresolved target (bad element index or missing coordinates)"));
                    _options.Log?.Invoke($"{action.Type}: unresolved target — stopping batch");
                    break;
                }
                if (action.Type == AgentActionType.Drag && dest is null)
                {
                    history.Add(new AgentStep(action, false, "drag: unresolved destination"));
                    break;
                }

                // Async confirmation on the RESOLVED action.
                if (_options.Confirm is { } confirm)
                {
                    var request = new ActionConfirmation(
                        action.Type, target?.X, target?.Y, dest?.X, dest?.Y,
                        action.Button, action.Text, action.Key, Describe(action, target, dest));

                    var decision = await confirm(request);
                    if (decision == ConfirmDecision.Abort)
                        return new AgentRunResult(AgentRunOutcome.Aborted, "Aborted at confirmation", history);
                    if (decision == ConfirmDecision.Deny)
                    {
                        history.Add(new AgentStep(action, false, "denied — model will re-plan"));
                        _options.Log?.Invoke($"{action.Type}: denied");
                        break;
                    }
                }

                var (ok, detail) = await ExecuteAsync(action, target, dest, ct);
                history.Add(new AgentStep(action, ok, detail));
                _options.Log?.Invoke($"step {history.Count}: {action.Type} -> {(ok ? "ok" : "FAIL")} ({detail})");

                if (!ok)
                    break; // screen state is now uncertain — re-capture next turn

                if (++executed >= _options.MaxActions)
                    return new AgentRunResult(AgentRunOutcome.MaxActionsReached, $"Hit MaxActions={_options.MaxActions}", history);

                if (_options.StepDelayMs > 0)
                    await Task.Delay(_options.StepDelayMs, ct);
            }
        }

        return new AgentRunResult(AgentRunOutcome.MaxStepsReached, $"Hit MaxSteps={_options.MaxSteps}", history);
    }

    private async Task<(bool Ok, string Detail)> ExecuteAsync(
        AgentAction a, (int X, int Y)? target, (int X, int Y)? dest, CancellationToken ct)
    {
        if (_options.DryRun)
        {
            _options.Log?.Invoke($"[dry-run] {Describe(a, target, dest)}");
            return (true, "dry-run");
        }

        switch (a.Type)
        {
            case AgentActionType.Move:
                await _driver.MoveMouseAsync(target!.Value.X, target.Value.Y, ct);
                return (true, "ok");

            case AgentActionType.Click:
                await _driver.MoveMouseAsync(target!.Value.X, target.Value.Y, ct);
                await _driver.ClickAsync(a.Button, ct);
                return (true, "ok");

            case AgentActionType.DoubleClick:
                await _driver.MoveMouseAsync(target!.Value.X, target.Value.Y, ct);
                await _driver.DoubleClickAsync(a.Button, ct);
                return (true, "ok");

            case AgentActionType.Drag:
                await _driver.DragAsync(target!.Value.X, target.Value.Y, dest!.Value.X, dest.Value.Y, a.Button, ct);
                return (true, "ok");

            case AgentActionType.Type:
                if (string.IsNullOrEmpty(a.Text)) return (false, "type: empty text");
                await _driver.TypeTextAsync(a.Text, ct);
                return (true, "ok");

            case AgentActionType.Key:
                if (string.IsNullOrEmpty(a.Key)) return (false, "key: empty combo");
                await _driver.KeyPressAsync(a.Key, ct);
                return (true, "ok");

            case AgentActionType.Scroll:
                await _driver.ScrollAsync(a.ScrollDx ?? 0, a.ScrollDy ?? 0, ct);
                return (true, "ok");

            case AgentActionType.Wait:
                await Task.Delay(Math.Clamp(a.WaitMs ?? 500, 0, 10_000), ct);
                return (true, "ok");

            case AgentActionType.Screenshot:
                return (true, "ok"); // a fresh capture happens at the top of the next turn

            default:
                return (false, $"unhandled: {a.Type}");
        }
    }

    private static bool NeedsTarget(AgentActionType t) =>
        t is AgentActionType.Move or AgentActionType.Click or AgentActionType.DoubleClick or AgentActionType.Drag;

    /// <summary>Resolve an element index to its centre, else raw X/Y. Null = unresolved (never guessed).</summary>
    private static (int X, int Y)? ResolveTarget(int? element, int? x, int? y, IReadOnlyList<UiElement> elements)
    {
        if (element is { } idx)
        {
            foreach (var el in elements)
            {
                if (el.Index == idx)
                    return el.Center;
            }

            return null; // index given but not found — do NOT fall back to raw coordinates
        }

        if (x is { } px && y is { } py)
            return (px, py);

        return null;
    }

    private static string Describe(AgentAction a, (int X, int Y)? target, (int X, int Y)? dest) => a.Type switch
    {
        AgentActionType.Type => $"type \"{a.Text}\"",
        AgentActionType.Key => $"key {a.Key}",
        AgentActionType.Drag => $"drag ({target?.X},{target?.Y}) -> ({dest?.X},{dest?.Y})",
        AgentActionType.Scroll => $"scroll dx={a.ScrollDx ?? 0} dy={a.ScrollDy ?? 0}",
        AgentActionType.Wait => $"wait {a.WaitMs ?? 500}ms",
        _ => target is { } t ? $"{a.Type} @ ({t.X},{t.Y})" : $"{a.Type}",
    };
}
