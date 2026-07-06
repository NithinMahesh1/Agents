using Agents.Cli;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Argument-parsing and command-dispatch tests for the CLI. These deliberately exercise ONLY the paths
/// that return without real I/O — help / usage / error branches and command routing. A <i>successful</i>
/// <c>run</c> parse is not asserted here because a valid goal falls straight through into arming the kill
/// switch, creating the desktop driver, and talking to Ollama (see the testability note in the summary).
/// The trick used throughout: a trailing <c>--help</c> makes <see cref="RunCommand"/> return 0 before any
/// I/O, so a flag that parses cleanly can be proven accepted without ever executing a run.
/// </summary>
[Collection("Cli")]
public class CliArgParseTests
{
    // ---- Root dispatch (CliApp.RunAsync) ----------------------------------------------------

    [Fact]
    public async Task No_arguments_prints_root_help_and_returns_1()
    {
        var r = await Capture(() => CliApp.RunAsync([]));
        r.Code.ShouldBe(1);
        r.Out.ShouldContain("USAGE");
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Help_command_returns_0(string word)
    {
        var r = await Capture(() => CliApp.RunAsync([word]));
        r.Code.ShouldBe(0);
        r.Out.ShouldContain("COMMANDS");
    }

    [Fact]
    public async Task Unknown_command_returns_1_and_names_it()
    {
        var r = await Capture(() => CliApp.RunAsync(["frobnicate"]));
        r.Code.ShouldBe(1);
        r.Err.ShouldContain("unknown command 'frobnicate'");
    }

    [Fact]
    public async Task Run_route_dispatches_to_RunCommand_missing_goal_returns_2()
    {
        var r = await Capture(() => CliApp.RunAsync(["run"]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("a goal is required");
    }

    [Fact]
    public async Task Run_route_help_returns_0()
    {
        var r = await Capture(() => CliApp.RunAsync(["run", "--help"]));
        r.Code.ShouldBe(0);
        r.Out.ShouldContain("agents run");
    }

    [Fact]
    public async Task Driver_probe_route_help_returns_0()
    {
        var r = await Capture(() => CliApp.RunAsync(["driver-probe", "--help"]));
        r.Code.ShouldBe(0);
        r.Out.ShouldContain("driver-probe");
    }

    [Fact]
    public async Task Kill_route_help_returns_0()
    {
        var r = await Capture(() => CliApp.RunAsync(["kill", "--help"]));
        r.Code.ShouldBe(0);
        r.Out.ShouldContain("agents kill");
    }

    [Fact]
    public async Task Kill_route_with_no_running_agent_returns_1()
    {
        // Point the runtime dir at an empty temp dir so there is definitely no live control socket.
        var r = await CaptureWithEmptyRuntimeDir(() => CliApp.RunAsync(["kill"]));
        r.Code.ShouldBe(1);
        r.Out.ShouldContain("no running agent");
    }

    // ---- `run` flag parsing (RunCommand.RunAsync) -------------------------------------------

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public async Task Run_help_flag_prints_usage_and_returns_0(string flag)
    {
        var r = await Capture(() => RunCommand.RunAsync([flag]));
        r.Code.ShouldBe(0);
        r.Out.ShouldContain("OPTIONS");
    }

    [Fact]
    public async Task Run_without_a_goal_returns_2()
    {
        var r = await Capture(() => RunCommand.RunAsync([]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("a goal is required");
    }

    [Fact]
    public async Task Run_model_without_a_value_returns_2()
    {
        var r = await Capture(() => RunCommand.RunAsync(["--model"]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("--model needs a value");
    }

    [Fact]
    public async Task Run_model_consumes_the_next_token_even_when_flag_like()
    {
        // `--model` greedily takes the following token as its value, so `--help` becomes the model name
        // and the run then fails for the MISSING GOAL — proving the token was consumed, not treated as -h
        // (which would have returned 0).
        var r = await Capture(() => RunCommand.RunAsync(["--model", "--help"]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("a goal is required");
    }

    [Fact]
    public async Task Run_with_a_valid_model_value_is_accepted()
    {
        // --model consumes "llama3"; the trailing --help returns 0 before any real I/O.
        var r = await Capture(() => RunCommand.RunAsync(["--model", "llama3", "--help"]));
        r.Code.ShouldBe(0);
    }

    [Fact]
    public async Task Run_max_steps_without_a_value_returns_2()
    {
        var r = await Capture(() => RunCommand.RunAsync(["--max-steps"]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("--max-steps needs a value");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("3.5")]
    [InlineData("ten")]
    public async Task Run_max_steps_non_integer_returns_2(string value)
    {
        var r = await Capture(() => RunCommand.RunAsync(["--max-steps", value]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("must be a positive integer");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-99")]
    public async Task Run_max_steps_non_positive_returns_2(string value)
    {
        var r = await Capture(() => RunCommand.RunAsync(["--max-steps", value]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("must be a positive integer");
    }

    [Fact]
    public async Task Run_with_a_valid_max_steps_is_accepted()
    {
        var r = await Capture(() => RunCommand.RunAsync(["--max-steps", "5", "--help"]));
        r.Code.ShouldBe(0);
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("-z")]
    public async Task Run_unknown_option_returns_2(string flag)
    {
        var r = await Capture(() => RunCommand.RunAsync([flag]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("unknown option");
    }

    [Fact]
    public async Task Run_dry_run_flag_is_accepted()
    {
        // An unrecognised flag would error as an unknown option; the trailing --help proves --dry-run
        // parsed cleanly by returning 0.
        var r = await Capture(() => RunCommand.RunAsync(["--dry-run", "--help"]));
        r.Code.ShouldBe(0);
    }

    [Fact]
    public async Task Run_yolo_flag_is_accepted()
    {
        var r = await Capture(() => RunCommand.RunAsync(["--yolo", "--help"]));
        r.Code.ShouldBe(0);
    }

    [Fact]
    public async Task Run_all_flags_together_are_accepted()
    {
        var r = await Capture(() =>
            RunCommand.RunAsync(["--dry-run", "--yolo", "--model", "qwen", "--max-steps", "7", "--help"]));
        r.Code.ShouldBe(0);
    }

    [Fact]
    public async Task Run_more_than_one_goal_returns_2()
    {
        var r = await Capture(() => RunCommand.RunAsync(["first goal", "second goal"]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("only one goal");
    }

    [Fact]
    public async Task Run_double_dash_without_a_following_goal_returns_2()
    {
        var r = await Capture(() => RunCommand.RunAsync(["--"]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("expected a goal after --");
    }

    [Fact]
    public async Task Run_double_dash_captures_a_dash_leading_goal()
    {
        // `-- -dashy` makes "-dashy" the goal (not an unknown option); the extra bare token is then the
        // SECOND goal, which is what trips the error — proving `--` captured the dash-leading token
        // rather than rejecting it.
        var r = await Capture(() => RunCommand.RunAsync(["--", "-dashy", "extra"]));
        r.Code.ShouldBe(2);
        r.Err.ShouldContain("only one goal");
    }

    // ---- helpers ----------------------------------------------------------------------------

    private sealed record CliResult(int Code, string Out, string Err);

    /// <summary>Run a command with stdout/stderr captured, then restore the console.</summary>
    private static async Task<CliResult> Capture(Func<Task<int>> action)
    {
        var outPrev = Console.Out;
        var errPrev = Console.Error;
        var so = new StringWriter();
        var se = new StringWriter();
        Console.SetOut(so);
        Console.SetError(se);
        try
        {
            var code = await action();
            return new CliResult(code, so.ToString(), se.ToString());
        }
        finally
        {
            Console.SetOut(outPrev);
            Console.SetError(errPrev);
        }
    }

    /// <summary>
    /// As <see cref="Capture"/>, but with <c>XDG_RUNTIME_DIR</c> pointed at a throwaway empty dir so the
    /// kill switch's socket/handle lookup sees no live agent. Restores the env var and removes the dir.
    /// </summary>
    private static async Task<CliResult> CaptureWithEmptyRuntimeDir(Func<Task<int>> action)
    {
        var prev = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var dir = Path.Combine("/tmp", "agents-cli-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", dir);
        try
        {
            return await Capture(action);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", prev);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }
}

/// <summary>
/// Groups the console/env-mutating CLI test classes into one non-parallel collection so their
/// process-global <see cref="Console"/> and <c>XDG_RUNTIME_DIR</c> swaps never race one another (or the
/// rest of the suite). Referenced by <see cref="CliArgParseTests"/> and <c>CliSafetyTests</c>.
/// </summary>
[CollectionDefinition("Cli", DisableParallelization = true)]
public sealed class CliCollection
{
}
