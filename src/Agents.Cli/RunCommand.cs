using System.Runtime.InteropServices;
using Agents.Core;
using Agents.Desktop;
using Agents.Providers;

namespace Agents.Cli;

/// <summary>
/// <c>agents run "&lt;goal&gt;"</c> — wires the desktop driver, the Ollama model, and the agent loop
/// together behind the confirm-by-default gate and the kill switch, then reports the outcome.
/// </summary>
internal static class RunCommand
{
    private const string DefaultModel = "qwen2.5-vl";
    private static readonly int DefaultMaxSteps = new AgentLoopOptions().MaxSteps;

    public static async Task<int> RunAsync(string[] args)
    {
        string? goal = null;
        var modelName = DefaultModel;
        var dryRun = false;
        var yolo = false;
        int? maxSteps = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    Help.PrintRun();
                    return 0;

                case "--model":
                    if (i + 1 >= args.Length) return Usage("--model needs a value");
                    modelName = args[++i];
                    break;

                case "--max-steps":
                    if (i + 1 >= args.Length) return Usage("--max-steps needs a value");
                    if (!int.TryParse(args[++i], out var steps) || steps <= 0) return Usage("--max-steps must be a positive integer");
                    maxSteps = steps;
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                case "--yolo":
                    yolo = true;
                    break;

                case "--":
                    // Everything after -- is the goal, so a goal may start with '-'.
                    if (i + 1 >= args.Length) return Usage("expected a goal after --");
                    if (goal is not null) return Usage("only one goal is allowed (quote it as a single argument)");
                    goal = args[++i];
                    break;

                default:
                    if (arg.StartsWith('-')) return Usage($"unknown option '{arg}'");
                    if (goal is not null) return Usage("only one goal is allowed (quote it as a single argument)");
                    goal = arg;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(goal))
        {
            Help.PrintRun();
            return Usage("a goal is required");
        }

        var resolvedMaxSteps = maxSteps ?? DefaultMaxSteps;
        PrintBanner(modelName, goal, dryRun, yolo, resolvedMaxSteps);
        if (yolo && !dryRun)
        {
            PrintYoloWarning();
        }

        using var cts = new CancellationTokenSource();
        CliApp.WireCtrlC(cts);
        using var sigterm = TryRegisterSigterm(cts);

        KillSwitch killSwitch;
        try
        {
            killSwitch = KillSwitch.Arm(cts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"run: {ex.Message}");
            return 1;
        }

        using (killSwitch)
        {
            var driver = DriverFactory.Create();
            using var model = new OllamaProvider(modelName);

            var options = new AgentLoopOptions
            {
                MaxSteps = resolvedMaxSteps,
                DryRun = dryRun,
                // Dry runs execute nothing, so auto-allow (null) rather than nag the user per action.
                Confirm = dryRun ? null : yolo ? ConfirmGates.Yolo : ConfirmGates.Interactive,
                Log = CliApp.Log,
                // StepDelayMs is intentionally left at its 400 ms default — the human-reaction window.
            };

            var loop = new AgentLoop(driver, model, grounding: null, options);
            Console.WriteLine($"control socket: {RuntimePaths.KillSocketPath}");
            Console.WriteLine("stop anytime: Ctrl+C here, or `agents kill` from another terminal / a GNOME shortcut.");
            Console.WriteLine();

            AgentRunResult result;
            try
            {
                result = await loop.RunAsync(goal, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine();
                Console.WriteLine("run cancelled (kill switch / Ctrl+C).");
                return 130;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"run failed: {ex.Message}");
                return 1;
            }

            PrintResult(result);
            return result.Outcome == AgentRunOutcome.Completed ? 0 : 2;
        }
    }

    /// <summary>
    /// Best-effort SIGTERM handler so an external <c>kill &lt;pid&gt;</c> or a service stop cancels the
    /// run gracefully (cleanup runs, input is not left mid-action) instead of hard-terminating. Returns
    /// null on platforms where POSIX signals are unavailable; the socket kill switch and Ctrl+C remain.
    /// </summary>
    private static IDisposable? TryRegisterSigterm(CancellationTokenSource cts)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true; // stop the default terminate; we cancel the loop instead
                CliApp.Log("SIGTERM — cancelling run");
                cts.Cancel();
            });
        }
        catch
        {
            return null;
        }
    }

    private static void PrintBanner(string model, string goal, bool dryRun, bool yolo, int maxSteps)
    {
        var mode = dryRun ? "dry-run" : yolo ? "yolo (no confirmation)" : "confirm-by-default";
        Console.WriteLine("======================================================================");
        Console.WriteLine("  agents run — computer-use agent");
        Console.WriteLine($"  goal      : {goal}");
        Console.WriteLine($"  model     : ollama:{model}");
        Console.WriteLine($"  mode      : {mode}    max-steps: {maxSteps}");
        Console.WriteLine("  TRUST MODEL: your goal is trusted. EVERYTHING ON SCREEN IS UNTRUSTED.");
        Console.WriteLine("  On-screen text may try to steer the model (prompt injection); every");
        Console.WriteLine("  high-risk action asks for y/n/a confirmation unless --yolo is set.");
        Console.WriteLine("======================================================================");
    }

    private static void PrintYoloWarning()
    {
        Console.WriteLine();
        Console.WriteLine("!!! YOLO MODE: confirmation is DISABLED. The agent will click, type, and");
        Console.WriteLine("!!! press keys WITHOUT asking. On-screen prompt-injection can now drive your");
        Console.WriteLine("!!! machine. Keep a hand on `agents kill` / Ctrl+C.");
    }

    private static void PrintResult(AgentRunResult result)
    {
        Console.WriteLine();
        Console.WriteLine("----------------------------------------------------------------------");
        Console.WriteLine($"outcome : {result.Outcome}");
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            Console.WriteLine($"message : {result.Message}");
        }

        Console.WriteLine($"steps   : {result.History.Count}");
        var index = 1;
        foreach (var step in result.History)
        {
            Console.WriteLine($"  {index++,2}. {step.Action.Type,-11} {(step.Ok ? "ok " : "ERR")}  {step.Detail}");
        }

        Console.WriteLine("----------------------------------------------------------------------");
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine($"run: {message}");
        Console.Error.WriteLine("try `agents run --help`");
        return 2;
    }
}
