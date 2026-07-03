namespace Agents.Cli;

/// <summary>Static usage text for the CLI and each command.</summary>
internal static class Help
{
    public static void PrintRoot() => Console.WriteLine(
        """
        agents — a local computer-use agent (Wayland + ydotool, Ollama vision model)

        USAGE
          agents <command> [options]

        COMMANDS
          driver-probe        Check screen capture + input injection (no model needed).
          run "<goal>"        Drive the desktop toward <goal> (confirm-by-default).
          kill                Cancel a running `agents run`.
          help                Show this help.

        TRUST MODEL
          Your GOAL is trusted. EVERYTHING ON SCREEN IS UNTRUSTED: a page or app can
          try to steer the model (prompt injection). High-risk actions (click / type /
          key / drag) require your confirmation unless you pass --yolo.

        KILL SWITCH (Wayland-friendly)
          Wayland denies global hotkeys to ordinary apps, so bind `agents kill` yourself:
          GNOME Settings > Keyboard > View and Customize Shortcuts > Custom Shortcuts,
          add command  <abs-path-to>/agents kill  and assign a key (e.g. Ctrl+Alt+K).
          Pressing it cancels the current run between actions. Ctrl+C in the run's
          terminal works too.

        Run `agents <command> --help` for per-command options.
        """);

    public static void PrintRun() => Console.WriteLine(
        """
        agents run "<goal>" [options]

        Drives the desktop toward <goal> using the Ollama vision model, one action per
        turn, behind a confirm-by-default safety gate.

        OPTIONS
          --model <name>      Ollama model tag (default: qwen2.5-vl).
          --max-steps <n>     Max model turns before giving up (default: 25).
          --dry-run           Log actions but never execute them (auto-allows the gate).
          --yolo              Disable confirmation — auto-allow EVERY action. Dangerous.
          -h, --help          Show this help.

        CONFIRMATION
          Move / Scroll / Wait / Screenshot run automatically. Click / DoubleClick /
          Type / Key / Drag print the resolved action and wait for:
            y        allow the action
            n/Enter  skip it and let the model re-plan
            a        abort the whole run

        STOP A RUN
          Press Ctrl+C in this terminal, or run `agents kill` from anywhere (bind it to
          a GNOME custom keyboard shortcut so it works even when the agent has focus).
        """);

    public static void PrintProbe() => Console.WriteLine(
        """
        agents driver-probe

        Model-independent capability check. It:
          1. Captures three screenshots back-to-back, saving each to a temp dir outside
             the repo and printing its path, dimensions, and capture time.
          2. Warns if captures 2/3 are much slower than capture 1 — a sign the screenshot
             portal is prompting for permission on EVERY capture (which would make a
             looped run unusable; we would switch to ScreenCast + PipeWire instead).
          3. Moves the mouse to screen centre and types the fixed literal "agents-probe"
             into the focused window to confirm input injection works.

        OPTIONS
          -h, --help          Show this help.
        """);

    public static void PrintKill() => Console.WriteLine(
        """
        agents kill

        Cancels a running `agents run` by connecting to its control socket, which trips
        the run's cancellation token so the loop stops cleanly between actions. Prints a
        notice and exits non-zero if no agent is currently running.

        Bind this to a GNOME custom keyboard shortcut (Settings > Keyboard) for a
        Wayland-safe panic button that works even while the agent holds input focus.

        OPTIONS
          -h, --help          Show this help.
        """);
}
