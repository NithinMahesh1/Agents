using Agents.Core;
using Agents.Providers;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Return-value contract for <see cref="OllamaProvider.DecideAsync"/>: a well-formed Ollama envelope
/// yields the parsed actions (routed through the tolerant <see cref="AgentActionParser"/>); empty,
/// whitespace, null, or malformed content yields an EMPTY list — the loop feeds a corrective notice
/// and retries, so the provider never synthesizes a Fail; and genuine transport errors propagate
/// rather than being swallowed. The HTTP layer is stubbed — no live Ollama server is contacted.
/// </summary>
public class OllamaResponseTests
{
    private static AgentContext Context() => new()
    {
        Goal = "do the thing",
        Screen = new ScreenCapture(new byte[] { 1, 2, 3 }, new ScreenInfo(100, 100)),
    };

    private static OllamaProvider Provider(StubHttpMessageHandler handler)
        => new("qwen2.5-vl", "http://localhost:11434", new HttpClient(handler));

    // ---- Well-formed responses --------------------------------------------------------------

    [Fact]
    public async Task Returns_the_done_action_from_a_well_formed_response()
    {
        const string response = """{"message":{"content":"[{\"type\":\"done\",\"message\":\"all set\"}]"}}""";
        var provider = Provider(new StubHttpMessageHandler(response));

        var actions = await provider.DecideAsync(Context());

        var action = actions.ShouldHaveSingleItem();
        action.Type.ShouldBe(AgentActionType.Done);
        action.Message.ShouldBe("all set");
    }

    [Fact]
    public async Task Returns_the_click_action_with_coordinates_from_a_well_formed_response()
    {
        const string response = """{"message":{"content":"[{\"type\":\"click\",\"x\":10,\"y\":20}]"}}""";
        var provider = Provider(new StubHttpMessageHandler(response));

        var actions = await provider.DecideAsync(Context());

        var action = actions.ShouldHaveSingleItem();
        action.Type.ShouldBe(AgentActionType.Click);
        action.X.ShouldBe(10);
        action.Y.ShouldBe(20);
    }

    [Fact]
    public async Task Returns_all_actions_in_order_for_a_multi_action_response()
    {
        const string response =
            """{"message":{"content":"[{\"type\":\"move\",\"x\":1,\"y\":1},{\"type\":\"click\",\"x\":2,\"y\":2}]"}}""";
        var provider = Provider(new StubHttpMessageHandler(response));

        var actions = await provider.DecideAsync(Context());

        actions.Count.ShouldBe(2);
        actions[0].Type.ShouldBe(AgentActionType.Move);
        actions[1].Type.ShouldBe(AgentActionType.Click);
        actions[1].X.ShouldBe(2);
    }

    [Fact]
    public async Task Routes_content_through_the_tolerant_parser_stripping_markdown_fences()
    {
        // Weak local models often wrap the array in ```json fences; the provider must hand the raw
        // content to the tolerant parser rather than doing its own strict JSON parse.
        const string response = """{"message":{"content":"```json\n[{\"type\":\"done\"}]\n```"}}""";
        var provider = Provider(new StubHttpMessageHandler(response));

        var actions = await provider.DecideAsync(Context());

        actions.ShouldHaveSingleItem().Type.ShouldBe(AgentActionType.Done);
    }

    // ---- Unusable content → EMPTY list (loop retries; never a synthesized Fail) --------------

    [Theory]
    [InlineData("""{"message":{"content":""}}""")]                       // empty content
    [InlineData("""{"message":{"content":"   "}}""")]                    // whitespace-only content
    [InlineData("""{"message":{"content":null}}""")]                     // explicit null content
    [InlineData("""{"message":{}}""")]                                   // message object without content
    [InlineData("""{}""")]                                               // envelope without a message
    [InlineData("""{"message":{"content":"sorry, I can't do that"}}""")] // prose, no JSON actions
    public async Task Returns_empty_list_when_content_has_no_usable_action(string response)
    {
        var provider = Provider(new StubHttpMessageHandler(response));

        (await provider.DecideAsync(Context())).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""not a json envelope at all""")]     // body is not JSON
    [InlineData("""{"message":{"content":""")]         // truncated / malformed envelope
    [InlineData("""[1,2,3]""")]                        // valid JSON, wrong shape (array, not object)
    public async Task Returns_empty_list_for_a_malformed_json_envelope(string response)
    {
        var provider = Provider(new StubHttpMessageHandler(response));

        (await provider.DecideAsync(Context())).ShouldBeEmpty();
    }

    // ---- Transport failures propagate -------------------------------------------------------

    [Fact]
    public async Task Propagates_HttpRequestException_from_the_transport()
    {
        // Connection refused / DNS / timeout must NOT be swallowed into an empty list — the caller
        // needs to see genuine transport failures (empty means "model produced nothing", not "down").
        var handler = new StubHttpMessageHandler(
            _ => throw new HttpRequestException("connection refused"));
        var provider = Provider(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => provider.DecideAsync(Context()));
        ex.Message.ShouldContain("connection refused");
    }
}
