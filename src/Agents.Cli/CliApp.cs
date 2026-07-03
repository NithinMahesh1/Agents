namespace Agents.Cli;

/// <summary>Top-level command dispatch and helpers shared across commands.</summary>
internal static class CliApp
{
    /// <summary>Parse the command word and route to the matching command. Returns the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Help.PrintRoot();
            return 1;
        }

        var rest = args[1..];
        try
        {
            return args[0] switch
            {
                "driver-probe" => await DriverProbeCommand.RunAsync(rest),
                "run" => await RunCommand.RunAsync(rest),
                "kill" => KillCommand.Run(rest),
                "help" or "--help" or "-h" => PrintRootHelp(),
                _ => Unknown(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            // Keep it to the message — no stack trace to the console. The user IS the operator here,
            // so the message is useful, but we do not dump internals.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Indented progress sink shared by the loop log and the kill switch.</summary>
    public static void Log(string line) => Console.WriteLine($"  {line}");

    /// <summary>
    /// Wire Ctrl+C to graceful cancellation: the first press cancels the token (letting the loop unwind
    /// and cleanup run); a second press falls through to the runtime's default hard-terminate.
    /// </summary>
    public static void WireCtrlC(CancellationTokenSource cts)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested)
            {
                return; // second Ctrl+C -> leave e.Cancel = false so the process terminates
            }

            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("Ctrl+C — cancelling after the current action (press again to force quit)...");
            cts.Cancel();
        };
    }

    private static int PrintRootHelp()
    {
        Help.PrintRoot();
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'. Run `agents help`.");
        return 1;
    }
}
