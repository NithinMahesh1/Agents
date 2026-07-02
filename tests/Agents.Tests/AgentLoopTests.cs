using Agents.Core;
using NSubstitute;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Behavioural tests for <see cref="AgentLoop"/> — the capture → decide → execute cycle and its
/// safety gate. The driver, model, and grounding are mocked; every option sets StepDelayMs = 0 so
/// the loop runs synchronously fast.
/// </summary>
public class AgentLoopTests
{
    private const string Goal = "do the thing";

    /// <summary>A driver whose screen capture always succeeds; input methods are no-op stubs.</summary>
    private static IDesktopDriver MockDriver()
    {
        var driver = Substitute.For<IDesktopDriver>();
        driver.CaptureAsync(Arg.Any<CancellationToken>())
            .Returns(new ScreenCapture(new byte[] { 1, 2, 3 }, new ScreenInfo(100, 100)));
        return driver;
    }

    private static IReadOnlyList<AgentAction> Actions(params AgentAction[] actions) => actions;

    private static IReadOnlyList<UiElement> Elements(params UiElement[] elements) => elements;

    [Fact]
    public async Task Done_action_completes_run_with_its_message()
    {
        var driver = MockDriver();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(Actions(new AgentAction { Type = AgentActionType.Done, Message = "ok" }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        result.Message.ShouldBe("ok");
    }

    [Fact]
    public async Task Fail_action_returns_failed_outcome_with_message()
    {
        var driver = MockDriver();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(Actions(new AgentAction { Type = AgentActionType.Fail, Message = "nope" }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Failed);
        result.Message.ShouldBe("nope");
    }

    [Fact]
    public async Task Click_moves_to_coordinates_then_clicks_then_completes()
    {
        var driver = MockDriver();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(
                Actions(new AgentAction { Type = AgentActionType.Click, X = 10, Y = 20 }),
                Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        Received.InOrder(() =>
        {
            driver.MoveMouseAsync(10, 20, Arg.Any<CancellationToken>());
            driver.ClickAsync(MouseButton.Left, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Element_index_resolves_to_element_center_coordinates()
    {
        var driver = MockDriver();
        var grounding = Substitute.For<IGroundingProvider>();
        // Element at (40,50) sized 20x20 → centre (50,60).
        grounding.GetElementsAsync(Arg.Any<CancellationToken>())
            .Returns(Elements(new UiElement(1, "button", "OK", 40, 50, 20, 20)));

        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(
                Actions(new AgentAction { Type = AgentActionType.Click, Element = 1 }),
                Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, grounding, new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        await driver.Received().MoveMouseAsync(50, 60, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Loop_hits_max_steps_when_model_never_finishes()
    {
        var driver = MockDriver();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(Actions(new AgentAction { Type = AgentActionType.Move, X = 1, Y = 1 }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { MaxSteps = 3, StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.MaxStepsReached);
        result.History.Count.ShouldBe(3);
        await driver.Received(3).MoveMouseAsync(1, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dry_run_captures_the_screen_but_never_executes_input()
    {
        var driver = MockDriver();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(
                Actions(new AgentAction { Type = AgentActionType.Click, X = 5, Y = 5 }),
                Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { DryRun = true, StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        await driver.Received().CaptureAsync(Arg.Any<CancellationToken>());
        await driver.DidNotReceive().MoveMouseAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().ClickAsync(Arg.Any<MouseButton>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Safety_gate_veto_aborts_before_any_input_is_executed()
    {
        var driver = MockDriver();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(Actions(new AgentAction { Type = AgentActionType.Click, X = 5, Y = 5 }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions
        {
            StepDelayMs = 0,
            ConfirmAction = _ => false,
        });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Aborted);
        result.Message.ShouldBe("Vetoed by safety gate");
        await driver.DidNotReceive().ClickAsync(Arg.Any<MouseButton>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().MoveMouseAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Safety_gate_allowing_the_action_lets_it_execute()
    {
        var driver = MockDriver();
        var confirmed = new List<AgentActionType>();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(
                Actions(new AgentAction { Type = AgentActionType.Click, X = 7, Y = 8 }),
                Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions
        {
            StepDelayMs = 0,
            ConfirmAction = a => { confirmed.Add(a.Type); return true; },
        });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        confirmed.ShouldContain(AgentActionType.Click);
        await driver.Received().ClickAsync(MouseButton.Left, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Executed_action_is_recorded_in_history_but_done_is_not()
    {
        var driver = MockDriver();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(
                Actions(new AgentAction { Type = AgentActionType.Click, X = 10, Y = 20 }),
                Actions(new AgentAction { Type = AgentActionType.Done, Message = "ok" }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        // The terminal Done short-circuits before it is added, so only the Click is in history.
        result.History.ShouldHaveSingleItem();
        result.History[0].Action.Type.ShouldBe(AgentActionType.Click);
        result.History[0].Result.ShouldBe("ok");
    }

    [Fact]
    public async Task Type_action_forwards_text_to_the_driver()
    {
        var driver = MockDriver();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(
                Actions(new AgentAction { Type = AgentActionType.Type, Text = "hello world" }),
                Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        await loop.RunAsync(Goal);

        await driver.Received().TypeTextAsync("hello world", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_before_first_step_stops_the_loop()
    {
        var driver = MockDriver();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(Actions(new AgentAction { Type = AgentActionType.Done }));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });

        await Should.ThrowAsync<OperationCanceledException>(() => loop.RunAsync(Goal, cts.Token));
        await driver.DidNotReceive().CaptureAsync(Arg.Any<CancellationToken>());
    }
}
