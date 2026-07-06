using Agents.Cli;
using Agents.Core;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Tests for the two CLI safety mechanisms: the confirm-by-default gate (<see cref="ConfirmGates"/>) and
/// the out-of-process kill switch (<see cref="KillSwitch"/>).
///
/// <para>The gate reads the console directly (there is no injected input delegate/reader), so its input
/// is driven by swapping <see cref="Console.In"/>; an empty reader models EOF (a closed stdin / Ctrl+C at
/// the prompt). The kill switch binds a real Unix-domain socket, so each test points <c>XDG_RUNTIME_DIR</c>
/// at a short-lived temp dir (short so the socket path stays under the ~108-char <c>sun_path</c> limit) and
/// disposes the switch to unbind and delete the socket + handle files.</para>
/// </summary>
[Collection("Cli")]
public class CliSafetyTests
{
    // =========================================================================================
    //  ConfirmGates.Interactive — confirm-by-default gate
    // =========================================================================================

    // The canonical mapping the gate promises, verified across EVERY high-risk action type so both the
    // risk classification (Click/DoubleClick/Drag/Type/Key are consequential) and the y/a/blank/EOF
    // handling are covered together.
    [Theory]
    [InlineData(AgentActionType.Click, "y", ConfirmDecision.Allow)]
    [InlineData(AgentActionType.DoubleClick, "y", ConfirmDecision.Allow)]
    [InlineData(AgentActionType.Drag, "y", ConfirmDecision.Allow)]
    [InlineData(AgentActionType.Type, "y", ConfirmDecision.Allow)]
    [InlineData(AgentActionType.Key, "y", ConfirmDecision.Allow)]
    [InlineData(AgentActionType.Click, "a", ConfirmDecision.Abort)]
    [InlineData(AgentActionType.DoubleClick, "a", ConfirmDecision.Abort)]
    [InlineData(AgentActionType.Drag, "a", ConfirmDecision.Abort)]
    [InlineData(AgentActionType.Type, "a", ConfirmDecision.Abort)]
    [InlineData(AgentActionType.Key, "a", ConfirmDecision.Abort)]
    [InlineData(AgentActionType.Click, "\n", ConfirmDecision.Deny)]   // bare Enter -> skip & re-plan
    [InlineData(AgentActionType.Type, "\n", ConfirmDecision.Deny)]
    [InlineData(AgentActionType.Key, "\n", ConfirmDecision.Deny)]
    [InlineData(AgentActionType.Click, "", ConfirmDecision.Abort)]    // EOF (closed stdin / Ctrl+C)
    [InlineData(AgentActionType.Type, "", ConfirmDecision.Abort)]
    [InlineData(AgentActionType.Drag, "", ConfirmDecision.Abort)]
    public async Task Interactive_high_risk_action_maps_input_to_decision(
        AgentActionType type, string input, ConfirmDecision expected)
    {
        (await Interactive(type, input)).ShouldBe(expected);
    }

    // The gate lower-cases, trims, accepts the long forms, and treats anything else (n / no / arbitrary
    // text / whitespace-only) as Deny.
    [Theory]
    [InlineData("yes", ConfirmDecision.Allow)]
    [InlineData("Y", ConfirmDecision.Allow)]
    [InlineData("  y  ", ConfirmDecision.Allow)]
    [InlineData("abort", ConfirmDecision.Abort)]
    [InlineData("A", ConfirmDecision.Abort)]
    [InlineData("n", ConfirmDecision.Deny)]
    [InlineData("no", ConfirmDecision.Deny)]
    [InlineData("maybe", ConfirmDecision.Deny)]
    [InlineData("   ", ConfirmDecision.Deny)]   // whitespace trims to empty -> Deny (not EOF)
    public async Task Interactive_parses_input_leniently(string input, ConfirmDecision expected)
    {
        (await Interactive(AgentActionType.Click, input)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(AgentActionType.Move)]
    [InlineData(AgentActionType.Scroll)]
    [InlineData(AgentActionType.Wait)]
    [InlineData(AgentActionType.Screenshot)]
    public async Task Interactive_low_risk_action_auto_allows(AgentActionType type)
    {
        // Even an "a" on stdin (which would ABORT a high-risk action) leaves a low-risk action Allowed,
        // because the gate returns before it reads anything.
        (await Interactive(type, "a")).ShouldBe(ConfirmDecision.Allow);
    }

    [Fact]
    public async Task Interactive_low_risk_action_does_not_read_input()
    {
        var inPrev = Console.In;
        var outPrev = Console.Out;
        Console.SetIn(new StringReader("a\nstill-here"));
        Console.SetOut(TextWriter.Null);
        try
        {
            (await ConfirmGates.Interactive(Req(AgentActionType.Move))).ShouldBe(ConfirmDecision.Allow);
            // The gate never called ReadLine, so the first input line is still unread and readable here.
            Console.In.ReadLine().ShouldBe("a");
        }
        finally
        {
            Console.SetIn(inPrev);
            Console.SetOut(outPrev);
        }
    }

    // =========================================================================================
    //  ConfirmGates.Yolo — --yolo gate (auto-allow everything)
    // =========================================================================================

    [Theory]
    [InlineData(AgentActionType.Click)]
    [InlineData(AgentActionType.DoubleClick)]
    [InlineData(AgentActionType.Drag)]
    [InlineData(AgentActionType.Type)]
    [InlineData(AgentActionType.Key)]
    [InlineData(AgentActionType.Move)]
    [InlineData(AgentActionType.Scroll)]
    [InlineData(AgentActionType.Wait)]
    [InlineData(AgentActionType.Screenshot)]
    public async Task Yolo_allows_every_action(AgentActionType type)
    {
        var outPrev = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            (await ConfirmGates.Yolo(Req(type))).ShouldBe(ConfirmDecision.Allow);
        }
        finally
        {
            Console.SetOut(outPrev);
        }
    }

    [Fact]
    public async Task Yolo_never_reads_input()
    {
        var inPrev = Console.In;
        var outPrev = Console.Out;
        Console.SetIn(new StringReader("a"));   // would Abort a high-risk action if the gate read it
        Console.SetOut(TextWriter.Null);
        try
        {
            (await ConfirmGates.Yolo(Req(AgentActionType.Click))).ShouldBe(ConfirmDecision.Allow);
            Console.In.ReadLine().ShouldBe("a"); // untouched
        }
        finally
        {
            Console.SetIn(inPrev);
            Console.SetOut(outPrev);
        }
    }

    [Fact]
    public async Task Yolo_logs_high_risk_actions_but_stays_silent_for_low_risk()
    {
        var outPrev = Console.Out;
        try
        {
            var high = new StringWriter();
            Console.SetOut(high);
            await ConfirmGates.Yolo(Req(AgentActionType.Click));
            high.ToString().ShouldContain("yolo"); // audit trail for a consequential action

            var low = new StringWriter();
            Console.SetOut(low);
            await ConfirmGates.Yolo(Req(AgentActionType.Move));
            low.ToString().ShouldNotContain("yolo"); // low-risk actions are allowed silently
        }
        finally
        {
            Console.SetOut(outPrev);
        }
    }

    // =========================================================================================
    //  KillSwitch — out-of-process cancel via a Unix-domain control socket
    // =========================================================================================

    [Fact]
    public void Arm_binds_the_socket_and_writes_the_handle_file()
    {
        using var scope = new KillSwitchTestScope();
        using var cts = new CancellationTokenSource();
        using var killSwitch = KillSwitch.Arm(cts);

        File.Exists(RuntimePaths.KillSocketPath).ShouldBeTrue();
        File.Exists(RuntimePaths.HandleFilePath).ShouldBeTrue();

        var handle = File.ReadAllText(RuntimePaths.HandleFilePath);
        handle.ShouldContain(RuntimePaths.KillSocketPath);          // records the control-socket path
        handle.ShouldContain(Environment.ProcessId.ToString());     // records the owning pid
    }

    [Fact]
    public void Arm_refuses_a_second_live_instance()
    {
        using var scope = new KillSwitchTestScope();
        using var cts1 = new CancellationTokenSource();
        using var first = KillSwitch.Arm(cts1);

        using var cts2 = new CancellationTokenSource();
        var ex = Should.Throw<InvalidOperationException>(() => KillSwitch.Arm(cts2));
        ex.Message.ShouldContain("already live");
    }

    [Fact]
    public void Arm_liveness_probe_also_cancels_the_incumbent_run_KNOWN_BUG()
    {
        // DOCUMENTS A BUG — this asserts current behaviour, NOT desired behaviour.
        // Arm() detects a live peer by CONNECTING to the control socket, but any connection is exactly
        // what the kill switch treats as "cancel this run". So attempting a second `agents run` both
        // (correctly) refuses to start AND (wrongly) kills the running agent it was meant to protect.
        // If this is fixed, flip this test to assert cts1 is NOT cancelled.
        using var scope = new KillSwitchTestScope();
        using var cts1 = new CancellationTokenSource();
        using var first = KillSwitch.Arm(cts1);

        using var cts2 = new CancellationTokenSource();
        Should.Throw<InvalidOperationException>(() => KillSwitch.Arm(cts2));

        cts1.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        cts1.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Arm_cleans_up_a_stale_socket_file_from_a_crashed_run()
    {
        using var scope = new KillSwitchTestScope();
        // A leftover inode with nothing listening — what a crashed run leaves behind (bind() would
        // otherwise fail with EADDRINUSE).
        File.WriteAllText(RuntimePaths.KillSocketPath, "stale");

        using var cts = new CancellationTokenSource();
        using var killSwitch = KillSwitch.Arm(cts);   // must delete the stale inode and bind fresh

        File.Exists(RuntimePaths.KillSocketPath).ShouldBeTrue();
        // And the fresh listener is genuinely live: a signal connects and cancels the run.
        KillSwitch.Signal().ShouldBeTrue();
        cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)).ShouldBeTrue();
    }

    [Fact]
    public void Signal_with_no_running_agent_returns_false()
    {
        using var scope = new KillSwitchTestScope();
        KillSwitch.Signal().ShouldBeFalse();
    }

    [Fact]
    public void Signal_cancels_the_armed_run_token()
    {
        using var scope = new KillSwitchTestScope();
        using var cts = new CancellationTokenSource();
        using var killSwitch = KillSwitch.Arm(cts);

        cts.IsCancellationRequested.ShouldBeFalse();       // not tripped until a signal arrives
        KillSwitch.Signal().ShouldBeTrue();
        cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)).ShouldBeTrue();
    }

    [Fact]
    public void Signal_falls_back_to_the_socket_path_when_the_handle_file_is_missing()
    {
        using var scope = new KillSwitchTestScope();
        using var cts = new CancellationTokenSource();
        using var killSwitch = KillSwitch.Arm(cts);

        File.Delete(RuntimePaths.HandleFilePath);          // handle gone, but the well-known socket is live
        KillSwitch.Signal().ShouldBeTrue();                // falls back to RuntimePaths.KillSocketPath
        cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)).ShouldBeTrue();
    }

    [Fact]
    public void Dispose_removes_the_socket_and_handle_files()
    {
        using var scope = new KillSwitchTestScope();
        using var cts = new CancellationTokenSource();

        var killSwitch = KillSwitch.Arm(cts);
        File.Exists(RuntimePaths.KillSocketPath).ShouldBeTrue();
        File.Exists(RuntimePaths.HandleFilePath).ShouldBeTrue();

        killSwitch.Dispose();
        File.Exists(RuntimePaths.KillSocketPath).ShouldBeFalse();
        File.Exists(RuntimePaths.HandleFilePath).ShouldBeFalse();
    }

    [Fact]
    public void Arm_succeeds_again_after_the_previous_run_is_disposed()
    {
        using var scope = new KillSwitchTestScope();

        using (var cts1 = new CancellationTokenSource())
        using (KillSwitch.Arm(cts1))
        {
            // first run holds the control socket for the duration of this block
        }

        // Once released, a fresh run arms cleanly — the single-instance guard only blocks LIVE peers.
        using var cts2 = new CancellationTokenSource();
        using var second = KillSwitch.Arm(cts2);
        File.Exists(RuntimePaths.KillSocketPath).ShouldBeTrue();
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// A representative resolved action for the gate. Only <see cref="ActionConfirmation.Type"/> drives
    /// the decision; the coordinates/text/key are realistic filler so the printed prompt is well-formed.
    /// </summary>
    private static ActionConfirmation Req(AgentActionType type) => new(
        type,
        X: 100,
        Y: 200,
        ToX: type == AgentActionType.Drag ? 300 : null,
        ToY: type == AgentActionType.Drag ? 400 : null,
        Button: MouseButton.Left,
        Text: type == AgentActionType.Type ? "hello" : null,
        Key: type == AgentActionType.Key ? "ctrl+c" : null,
        Description: $"{type} test action");

    /// <summary>
    /// Drive <see cref="ConfirmGates.Interactive"/> with <paramref name="input"/> on stdin (an empty
    /// string models EOF), discarding the prompt it writes, and restore the console afterwards.
    /// </summary>
    private static async Task<ConfirmDecision> Interactive(AgentActionType type, string input)
    {
        var inPrev = Console.In;
        var outPrev = Console.Out;
        Console.SetIn(new StringReader(input));
        Console.SetOut(TextWriter.Null);
        try
        {
            return await ConfirmGates.Interactive(Req(type));
        }
        finally
        {
            Console.SetIn(inPrev);
            Console.SetOut(outPrev);
        }
    }

    /// <summary>
    /// Points <c>XDG_RUNTIME_DIR</c> at a fresh, short temp dir (Unix-socket paths must stay under the
    /// ~108-char <c>sun_path</c> limit, so <c>/tmp</c> is used rather than a long temp root) and silences
    /// the console for the duration of a KillSwitch test — the switch logs a line from a background accept
    /// task when a signal lands. Everything is restored/removed on <see cref="Dispose"/>.
    /// </summary>
    private sealed class KillSwitchTestScope : IDisposable
    {
        private readonly string? _prevRuntimeDir;
        private readonly TextWriter _prevOut;
        private readonly TextWriter _prevErr;
        private readonly string _dir;

        public KillSwitchTestScope()
        {
            _prevOut = Console.Out;
            _prevErr = Console.Error;
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);

            _prevRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            _dir = Path.Combine("/tmp", "agents-ks-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(_dir);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _dir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _prevRuntimeDir);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort cleanup */ }
            Console.SetOut(_prevOut);
            Console.SetError(_prevErr);
        }
    }
}
