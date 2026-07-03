namespace Agents.Cli;

/// <summary>
/// <c>agents kill</c> — the panic button. Connects to a running agent's control socket to cancel it.
/// Designed to be bound to a GNOME custom keyboard shortcut so it fires even while the agent has focus
/// (Wayland denies the agent itself a global hotkey).
/// </summary>
internal static class KillCommand
{
    public static int Run(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            Help.PrintKill();
            return 0;
        }

        if (KillSwitch.Signal())
        {
            Console.WriteLine("kill signal delivered — the running agent will stop between actions.");
            return 0;
        }

        Console.WriteLine("no running agent found (no live control socket).");
        return 1;
    }
}
