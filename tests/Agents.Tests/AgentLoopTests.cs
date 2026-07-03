using Agents.Core;
using NSubstitute;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Behavioural tests for <see cref="AgentLoop"/> — the capture → ground → decide → resolve → confirm →
/// execute cycle and its async safety gate. The driver, model, and grounding are mocked; every option
/// sets StepDelayMs = 0 so the loop runs synchronously fast.
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

    /// <summary>A model that returns the given batch on the first turn and the rest on later turns.</summary>
    private static IModelProvider ModelReturning(params IReadOnlyList<AgentAction>[] turns)
    {
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(turns[0], turns[1..]);
        return model;
    }

    // ---- Terminal actions -------------------------------------------------------------------

    [Fact]
    public async Task Done_action_completes_run_with_its_message()
    {
        var driver = MockDriver();
        var model = ModelReturning(Actions(new AgentAction { Type = AgentActionType.Done, Message = "ok" }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        result.Message.ShouldBe("ok");
        result.History.ShouldBeEmpty(); // terminal Done is never recorded as a step
    }

    [Fact]
    public async Task Fail_action_returns_failed_outcome_with_message()
    {
        var driver = MockDriver();
        var model = ModelReturning(Actions(new AgentAction { Type = AgentActionType.Fail, Message = "nope" }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Failed);
        result.Message.ShouldBe("nope");
    }

    // ---- Basic execution --------------------------------------------------------------------

    [Fact]
    public async Task Click_moves_to_coordinates_then_clicks_then_completes()
    {
        var driver = MockDriver();
        var model = ModelReturning(
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

        // The executed Click is recorded and marked successful; Done is not recorded.
        var step = result.History.ShouldHaveSingleItem();
        step.Action.Type.ShouldBe(AgentActionType.Click);
        step.Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Type_action_forwards_text_to_the_driver()
    {
        var driver = MockDriver();
        var model = ModelReturning(
            Actions(new AgentAction { Type = AgentActionType.Type, Text = "hello world" }),
            Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        await loop.RunAsync(Goal);

        await driver.Received().TypeTextAsync("hello world", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Executed_action_is_recorded_in_history_but_done_is_not()
    {
        var driver = MockDriver();
        var model = ModelReturning(
            Actions(new AgentAction { Type = AgentActionType.Click, X = 10, Y = 20 }),
            Actions(new AgentAction { Type = AgentActionType.Done, Message = "ok" }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        // The terminal Done short-circuits before it is added, so only the Click is in history.
        var step = result.History.ShouldHaveSingleItem();
        step.Action.Type.ShouldBe(AgentActionType.Click);
        step.Ok.ShouldBeTrue();
        step.Detail.ShouldBe("ok");
    }

    // ---- Element (Set-of-Marks) resolution --------------------------------------------------

    [Fact]
    public async Task Element_index_resolves_to_element_center_coordinates()
    {
        var driver = MockDriver();
        var grounding = Substitute.For<IGroundingProvider>();
        // Element at (40,50) sized 20x20 → centre (50,60).
        grounding.GetElementsAsync(Arg.Any<CancellationToken>())
            .Returns(Elements(new UiElement(1, "button", "OK", 40, 50, 20, 20)));

        var model = ModelReturning(
            Actions(new AgentAction { Type = AgentActionType.Click, Element = 1 }),
            Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, grounding, new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        await driver.Received().MoveMouseAsync(50, 60, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unresolved_element_index_never_blind_clicks_and_records_a_failed_step()
    {
        var driver = MockDriver();
        var grounding = Substitute.For<IGroundingProvider>();
        // Only element #1 exists; the model asks for a non-existent #99.
        grounding.GetElementsAsync(Arg.Any<CancellationToken>())
            .Returns(Elements(new UiElement(1, "button", "OK", 40, 50, 20, 20)));

        var model = ModelReturning(
            Actions(new AgentAction { Type = AgentActionType.Click, Element = 99 }),
            Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, grounding, new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        // The loop must NOT fall back to any coordinate — no mouse movement or click at all.
        await driver.DidNotReceive().MoveMouseAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().ClickAsync(Arg.Any<MouseButton>(), Arg.Any<CancellationToken>());

        var failed = result.History.ShouldHaveSingleItem();
        failed.Action.Type.ShouldBe(AgentActionType.Click);
        failed.Ok.ShouldBeFalse();
    }

    // ---- Async confirmation gate ------------------------------------------------------------

    [Fact]
    public async Task Confirm_allow_executes_and_gate_sees_resolved_element_coordinates()
    {
        var driver = MockDriver();
        var grounding = Substitute.For<IGroundingProvider>();
        grounding.GetElementsAsync(Arg.Any<CancellationToken>())
            .Returns(Elements(new UiElement(1, "button", "OK", 40, 50, 20, 20))); // centre (50,60)

        var model = ModelReturning(
            Actions(new AgentAction { Type = AgentActionType.Click, Element = 1 }),
            Actions(new AgentAction { Type = AgentActionType.Done }));

        ActionConfirmation? seen = null;
        var loop = new AgentLoop(driver, model, grounding, new AgentLoopOptions
        {
            StepDelayMs = 0,
            Confirm = async req => { seen = req; await Task.Yield(); return ConfirmDecision.Allow; },
        });

        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        seen.ShouldNotBeNull();
        seen!.Type.ShouldBe(AgentActionType.Click);
        // The gate is handed the RESOLVED coordinates (element centre), not the raw element index.
        seen.X.ShouldBe(50);
        seen.Y.ShouldBe(60);
        await driver.Received().MoveMouseAsync(50, 60, Arg.Any<CancellationToken>());
        await driver.Received().ClickAsync(MouseButton.Left, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_deny_skips_the_action_but_run_continues_and_replans()
    {
        var driver = MockDriver();
        var model = ModelReturning(
            Actions(new AgentAction { Type = AgentActionType.Click, X = 7, Y = 8 }),
            Actions(new AgentAction { Type = AgentActionType.Done, Message = "done anyway" }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions
        {
            StepDelayMs = 0,
            Confirm = _ => new ValueTask<ConfirmDecision>(ConfirmDecision.Deny),
        });

        var result = await loop.RunAsync(Goal);

        // Deny is a skip, not a kill: the loop re-plans and reaches Done on the next turn.
        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        result.Message.ShouldBe("done anyway");
        await model.Received(2).DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().MoveMouseAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().ClickAsync(Arg.Any<MouseButton>(), Arg.Any<CancellationToken>());

        var denied = result.History.ShouldHaveSingleItem();
        denied.Action.Type.ShouldBe(AgentActionType.Click);
        denied.Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task Confirm_abort_ends_the_run_immediately_without_executing()
    {
        var driver = MockDriver();
        var model = ModelReturning(Actions(new AgentAction { Type = AgentActionType.Click, X = 7, Y = 8 }));

        ActionConfirmation? seen = null;
        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions
        {
            StepDelayMs = 0,
            Confirm = req => { seen = req; return new ValueTask<ConfirmDecision>(ConfirmDecision.Abort); },
        });

        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Aborted);
        // The gate still saw the resolved coordinates before the kill.
        seen.ShouldNotBeNull();
        seen!.X.ShouldBe(7);
        seen.Y.ShouldBe(8);
        await driver.DidNotReceive().MoveMouseAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().ClickAsync(Arg.Any<MouseButton>(), Arg.Any<CancellationToken>());
    }

    // ---- Drag -------------------------------------------------------------------------------

    [Fact]
    public async Task Drag_forwards_start_and_destination_to_the_driver()
    {
        var driver = MockDriver();
        var model = ModelReturning(
            Actions(new AgentAction { Type = AgentActionType.Drag, X = 10, Y = 10, ToX = 20, ToY = 20 }),
            Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        await driver.Received().DragAsync(10, 10, 20, 20, MouseButton.Left, Arg.Any<CancellationToken>());
        result.History.ShouldHaveSingleItem().Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Drag_with_unresolved_destination_never_drags_and_records_a_failed_step()
    {
        var driver = MockDriver();
        // Start resolves (10,10) but there is no toX/toY/toElement → destination cannot be resolved.
        var model = ModelReturning(
            Actions(new AgentAction { Type = AgentActionType.Drag, X = 10, Y = 10 }),
            Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        await driver.DidNotReceive().DragAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<MouseButton>(), Arg.Any<CancellationToken>());

        var failed = result.History.ShouldHaveSingleItem();
        failed.Action.Type.ShouldBe(AgentActionType.Drag);
        failed.Ok.ShouldBeFalse();
    }

    // ---- Limits -----------------------------------------------------------------------------

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
    public async Task Loop_hits_max_actions_independently_of_steps()
    {
        var driver = MockDriver();
        var model = Substitute.For<IModelProvider>();
        // Every turn yields one valid, executable Move — nothing ever terminates the run.
        model.DecideAsync(Arg.Any<AgentContext>(), Arg.Any<CancellationToken>())
            .Returns(Actions(new AgentAction { Type = AgentActionType.Move, X = 1, Y = 1 }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions
        {
            MaxActions = 2,
            MaxSteps = 100, // ensure MaxActions is the limit that fires
            StepDelayMs = 0,
        });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.MaxActionsReached);
        await driver.Received(2).MoveMouseAsync(1, 1, Arg.Any<CancellationToken>());
    }

    // ---- Empty / unparseable responses ------------------------------------------------------

    [Fact]
    public async Task Repeated_empty_responses_send_a_notice_then_fail()
    {
        var driver = MockDriver();

        // Capture every context the loop hands the model so we can inspect the corrective Notice.
        var contexts = new List<AgentContext>();
        var model = Substitute.For<IModelProvider>();
        model.DecideAsync(Arg.Do<AgentContext>(c => contexts.Add(c)), Arg.Any<CancellationToken>())
            .Returns(Actions()); // always empty → nothing usable

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions
        {
            MaxConsecutiveEmpty = 3,
            StepDelayMs = 0,
        });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Failed);

        // First turn has no notice; the SECOND turn carries the self-correction notice.
        contexts.Count.ShouldBeGreaterThanOrEqualTo(2);
        contexts[0].Notice.ShouldBeNull();
        contexts[1].Notice.ShouldNotBeNull();
    }

    // ---- Dry run ----------------------------------------------------------------------------

    [Fact]
    public async Task Dry_run_captures_the_screen_but_never_executes_any_input()
    {
        var driver = MockDriver();
        var model = ModelReturning(
            Actions(new AgentAction { Type = AgentActionType.Click, X = 5, Y = 5 }),
            Actions(new AgentAction { Type = AgentActionType.Type, Text = "hi" }),
            Actions(new AgentAction { Type = AgentActionType.Drag, X = 1, Y = 1, ToX = 2, ToY = 2 }),
            Actions(new AgentAction { Type = AgentActionType.Done }));

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { DryRun = true, StepDelayMs = 0 });
        var result = await loop.RunAsync(Goal);

        result.Outcome.ShouldBe(AgentRunOutcome.Completed);
        await driver.Received().CaptureAsync(Arg.Any<CancellationToken>());
        await driver.DidNotReceive().MoveMouseAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().ClickAsync(Arg.Any<MouseButton>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().TypeTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().DragAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<MouseButton>(), Arg.Any<CancellationToken>());
    }

    // ---- Cancellation -----------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_before_first_step_stops_the_loop_before_capture()
    {
        var driver = MockDriver();
        var model = ModelReturning(Actions(new AgentAction { Type = AgentActionType.Done }));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var loop = new AgentLoop(driver, model, options: new AgentLoopOptions { StepDelayMs = 0 });

        await Should.ThrowAsync<OperationCanceledException>(() => loop.RunAsync(Goal, cts.Token));
        await driver.DidNotReceive().CaptureAsync(Arg.Any<CancellationToken>());
    }
}
