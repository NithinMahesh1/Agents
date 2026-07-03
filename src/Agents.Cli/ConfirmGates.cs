using Agents.Core;

namespace Agents.Cli;

/// <summary>
/// The safety gate wired into <see cref="AgentLoopOptions.Confirm"/>. The loop calls it once per
/// action AFTER coordinates are resolved, so the human sees the REAL target before anything runs.
/// This is the primary defence against on-screen prompt injection: the goal is trusted, but whatever
/// the model was steered into doing on an untrusted screen is not.
/// </summary>
internal static class ConfirmGates
{
    /// <summary>
    /// Actions that change machine state irreversibly enough to require a human OK. Move / Scroll /
    /// Wait / Screenshot are auto-allowed because they neither click nor emit keystrokes.
    /// </summary>
    private static bool IsHighRisk(AgentActionType type) =>
        type is AgentActionType.Click
             or AgentActionType.DoubleClick
             or AgentActionType.Drag
             or AgentActionType.Type
             or AgentActionType.Key;

    /// <summary>
    /// Confirm-by-default gate: auto-allows low-risk actions and prompts on high-risk ones, mapping
    /// <c>y</c> → Allow, <c>a</c> → Abort (kill the run), and anything else (incl. a bare Enter) →
    /// Deny (skip this action and let the model re-plan).
    /// </summary>
    public static ValueTask<ConfirmDecision> Interactive(ActionConfirmation request)
    {
        if (!IsHighRisk(request.Type))
        {
            return ValueTask.FromResult(ConfirmDecision.Allow);
        }

        Console.WriteLine();
        Console.WriteLine($"  CONFIRM  {request.Description}");
        if (request.X is int x && request.Y is int y)
        {
            var destination = request.ToX is int tx && request.ToY is int ty ? $" -> ({tx},{ty})" : string.Empty;
            Console.WriteLine($"           target ({x},{y}){destination}");
        }

        Console.Write("           allow? [y]es / [N]o=skip & re-plan / [a]bort > ");

        // A blocking read is fine here: the loop is meant to pause for the human. If Ctrl+C is
        // pressed at the prompt, ReadLine returns null on cancellation, which we treat as Abort.
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        var decision = answer switch
        {
            "y" or "yes" => ConfirmDecision.Allow,
            "a" or "abort" => ConfirmDecision.Abort,
            null => ConfirmDecision.Abort,
            _ => ConfirmDecision.Deny,
        };

        return ValueTask.FromResult(decision);
    }

    /// <summary>
    /// <c>--yolo</c> gate: allows everything, echoing each high-risk action so there is still an audit
    /// trail in the log. Only reachable after the loud startup warning.
    /// </summary>
    public static ValueTask<ConfirmDecision> Yolo(ActionConfirmation request)
    {
        if (IsHighRisk(request.Type))
        {
            Console.WriteLine($"  [yolo] auto-allow {request.Description}");
        }

        return ValueTask.FromResult(ConfirmDecision.Allow);
    }
}
