using Agents.Core;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Contract tests for <see cref="AgentActionParser"/> — the boundary where untrusted model
/// text becomes typed actions. Covers enum/field mapping, fenced-block/prose extraction, the
/// single-object vs array shapes, and the "reject anything malformed" guarantee.
/// </summary>
public class AgentActionParserTests
{
    [Fact]
    public void Parses_json_array_of_multiple_actions_with_all_fields()
    {
        const string json = """
            [
              {"type":"move","x":5,"y":6},
              {"type":"click","x":10,"y":20,"button":"left"},
              {"type":"doubleClick","x":1,"y":2,"button":"right"},
              {"type":"type","text":"hello"},
              {"type":"key","key":"ctrl+c"},
              {"type":"done","message":"finished"}
            ]
            """;

        var actions = AgentActionParser.Parse(json);

        actions.Count.ShouldBe(6);

        actions[0].Type.ShouldBe(AgentActionType.Move);
        actions[0].X.ShouldBe(5);
        actions[0].Y.ShouldBe(6);

        actions[1].Type.ShouldBe(AgentActionType.Click);
        actions[1].X.ShouldBe(10);
        actions[1].Y.ShouldBe(20);
        actions[1].Button.ShouldBe(MouseButton.Left);

        actions[2].Type.ShouldBe(AgentActionType.DoubleClick);
        actions[2].Button.ShouldBe(MouseButton.Right);

        actions[3].Type.ShouldBe(AgentActionType.Type);
        actions[3].Text.ShouldBe("hello");

        actions[4].Type.ShouldBe(AgentActionType.Key);
        actions[4].Key.ShouldBe("ctrl+c");

        actions[5].Type.ShouldBe(AgentActionType.Done);
        actions[5].Message.ShouldBe("finished");
    }

    [Theory]
    [InlineData("move", AgentActionType.Move)]
    [InlineData("click", AgentActionType.Click)]
    [InlineData("doubleClick", AgentActionType.DoubleClick)]
    [InlineData("type", AgentActionType.Type)]
    [InlineData("key", AgentActionType.Key)]
    [InlineData("scroll", AgentActionType.Scroll)]
    [InlineData("wait", AgentActionType.Wait)]
    [InlineData("screenshot", AgentActionType.Screenshot)]
    [InlineData("done", AgentActionType.Done)]
    [InlineData("fail", AgentActionType.Fail)]
    public void Maps_camelCase_type_strings_to_enum(string typeText, AgentActionType expected)
    {
        var actions = AgentActionParser.Parse($$"""[{"type":"{{typeText}}"}]""");

        actions.ShouldHaveSingleItem().Type.ShouldBe(expected);
    }

    [Theory]
    [InlineData("left", MouseButton.Left)]
    [InlineData("right", MouseButton.Right)]
    [InlineData("middle", MouseButton.Middle)]
    public void Maps_button_strings_to_enum(string buttonText, MouseButton expected)
    {
        var actions = AgentActionParser.Parse($$"""[{"type":"click","button":"{{buttonText}}"}]""");

        actions.ShouldHaveSingleItem().Button.ShouldBe(expected);
    }

    [Fact]
    public void Button_defaults_to_left_when_omitted()
    {
        var actions = AgentActionParser.Parse("""[{"type":"click","x":1,"y":2}]""");

        actions.ShouldHaveSingleItem().Button.ShouldBe(MouseButton.Left);
    }

    [Fact]
    public void Parses_single_object_into_one_action()
    {
        var actions = AgentActionParser.Parse("""{"type":"click","x":3,"y":4}""");

        var action = actions.ShouldHaveSingleItem();
        action.Type.ShouldBe(AgentActionType.Click);
        action.X.ShouldBe(3);
        action.Y.ShouldBe(4);
    }

    [Fact]
    public void Strips_fenced_json_block_and_surrounding_prose()
    {
        const string output = """
            Here is my plan for this step:
            ```json
            [{"type":"done","message":"ok"}]
            ```
            Hope that helps!
            """;

        var actions = AgentActionParser.Parse(output);

        var action = actions.ShouldHaveSingleItem();
        action.Type.ShouldBe(AgentActionType.Done);
        action.Message.ShouldBe("ok");
    }

    [Fact]
    public void Strips_bare_prose_around_a_json_array()
    {
        var actions = AgentActionParser.Parse("""Sure! [{"type":"click","x":1,"y":2}] happy to help""");

        var action = actions.ShouldHaveSingleItem();
        action.Type.ShouldBe(AgentActionType.Click);
        action.X.ShouldBe(1);
        action.Y.ShouldBe(2);
    }

    [Fact]
    public void Handles_plain_language_fence_without_json_hint()
    {
        const string output = """
            ```
            {"type":"key","key":"Return"}
            ```
            """;

        var actions = AgentActionParser.Parse(output);

        actions.ShouldHaveSingleItem().Key.ShouldBe("Return");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not json at all")]
    [InlineData("[]")]                              // empty array -> nothing
    [InlineData("""{"foo":"bar"}""")]              // object missing required "type"
    [InlineData("""[{"x":1,"y":2}]""")]            // array element missing required "type"
    [InlineData("""[{"type":"frobnicate"}]""")]    // unknown enum value
    [InlineData("""{"type":"click","x":""")]       // truncated / malformed json
    public void Returns_empty_list_for_garbage_or_missing_type(string input)
    {
        AgentActionParser.Parse(input).ShouldBeEmpty();
    }

    [Fact]
    public void Parses_element_index_action_with_coordinates_and_button()
    {
        var actions = AgentActionParser.Parse(
            """[{"type":"click","element":3,"x":100,"y":200,"button":"middle"}]""");

        var action = actions.ShouldHaveSingleItem();
        action.Type.ShouldBe(AgentActionType.Click);
        action.Element.ShouldBe(3);
        action.X.ShouldBe(100);
        action.Y.ShouldBe(200);
        action.Button.ShouldBe(MouseButton.Middle);
    }

    [Fact]
    public void Element_is_null_when_only_coordinates_are_given()
    {
        var actions = AgentActionParser.Parse("""[{"type":"click","x":7,"y":8}]""");

        actions.ShouldHaveSingleItem().Element.ShouldBeNull();
    }

    [Fact]
    public void Parses_scroll_and_wait_fields()
    {
        var actions = AgentActionParser.Parse("""
            [{"type":"scroll","scrollDx":5,"scrollDy":-10},
             {"type":"wait","waitMs":250}]
            """);

        actions.Count.ShouldBe(2);
        actions[0].ScrollDx.ShouldBe(5);
        actions[0].ScrollDy.ShouldBe(-10);
        actions[1].WaitMs.ShouldBe(250);
    }
}
