using System.Text.Json;
using Agents.Core;
using Agents.Providers;
using Shouldly;

namespace Agents.Tests;

/// <summary>
/// Contract tests for the request <see cref="OllamaProvider"/> builds and POSTs to Ollama's
/// <c>/api/chat</c>: the endpoint/method, the wire body (model, stream:false, format:"json"), and
/// how an <see cref="AgentContext"/> — goal, screenshot, numbered elements, history, corrective
/// notice — is rendered into the system + user messages. The transport is stubbed via
/// <see cref="StubHttpMessageHandler"/>, so no live Ollama server is contacted.
/// </summary>
public class OllamaRequestTests
{
    private const string Model = "qwen2.5-vl";
    private const string BaseUrl = "http://localhost:11434";

    // A valid, minimal Ollama envelope so DecideAsync completes; these tests assert on the REQUEST.
    private const string DoneResponse = """{"message":{"content":"[{\"type\":\"done\"}]"}}""";

    private static readonly byte[] ScreenshotBytes = { 1, 2, 3 };

    /// <summary>Drive one DecideAsync and hand back the stub so its captured request can be asserted.</summary>
    private static async Task<StubHttpMessageHandler> CaptureRequestAsync(
        AgentContext context, string baseUrl = BaseUrl)
    {
        var handler = new StubHttpMessageHandler(DoneResponse);
        var provider = new OllamaProvider(Model, baseUrl, new HttpClient(handler));
        await provider.DecideAsync(context);
        return handler;
    }

    private static AgentContext Context(
        string goal = "open the file menu",
        IReadOnlyList<UiElement>? elements = null,
        IReadOnlyList<AgentStep>? history = null,
        string? notice = null) => new()
    {
        Goal = goal,
        Screen = new ScreenCapture(ScreenshotBytes, new ScreenInfo(100, 100)),
        Elements = elements ?? [],
        History = history ?? [],
        Notice = notice,
    };

    // ---- Endpoint & method ------------------------------------------------------------------

    [Fact]
    public async Task Posts_to_the_absolute_api_chat_endpoint_as_json()
    {
        var handler = await CaptureRequestAsync(Context());

        var request = handler.LastRequest;
        request.ShouldNotBeNull();
        request!.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe($"{BaseUrl}/api/chat");
        request.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public async Task Targets_the_configured_base_url_rather_than_a_hardcoded_host()
    {
        var handler = await CaptureRequestAsync(Context(), "http://model-host:9999");

        handler.LastRequest!.RequestUri!.ToString().ShouldBe("http://model-host:9999/api/chat");
    }

    [Fact]
    public async Task Normalizes_a_trailing_slash_in_the_base_url()
    {
        var handler = await CaptureRequestAsync(Context(), "http://localhost:11434/");

        handler.LastRequest!.RequestUri!.ToString().ShouldBe("http://localhost:11434/api/chat");
    }

    // ---- Top-level body fields --------------------------------------------------------------

    [Fact]
    public async Task Body_sets_model_stream_false_and_json_format()
    {
        var handler = await CaptureRequestAsync(Context());

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;

        root.GetProperty("model").GetString().ShouldBe(Model);
        root.GetProperty("stream").GetBoolean().ShouldBeFalse();
        root.GetProperty("format").GetString().ShouldBe("json");
    }

    // ---- System message ---------------------------------------------------------------------

    [Fact]
    public async Task First_message_is_the_system_prompt_carrying_the_action_schema()
    {
        var handler = await CaptureRequestAsync(Context());

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var system = doc.RootElement.GetProperty("messages")[0];

        system.GetProperty("role").GetString().ShouldBe("system");
        // The full schema the parser publishes is embedded verbatim in the system prompt.
        system.GetProperty("content").GetString()!.ShouldContain(AgentActionParser.SchemaPrompt);
        // The system message is text-only — no screenshot attached.
        system.TryGetProperty("images", out _).ShouldBeFalse();
    }

    // ---- User message: goal + screenshot ----------------------------------------------------

    [Fact]
    public async Task Second_message_is_the_user_turn_including_the_goal()
    {
        var handler = await CaptureRequestAsync(Context(goal: "click the Save button"));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(2);

        var user = messages[1];
        user.GetProperty("role").GetString().ShouldBe("user");
        user.GetProperty("content").GetString()!.ShouldContain("click the Save button");
    }

    [Fact]
    public async Task User_message_carries_the_screenshot_as_base64_in_images()
    {
        var handler = await CaptureRequestAsync(Context());

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var user = doc.RootElement.GetProperty("messages")[1];

        var images = user.GetProperty("images");
        images.GetArrayLength().ShouldBe(1);
        images[0].GetString().ShouldBe(Convert.ToBase64String(ScreenshotBytes));
    }

    // ---- User message: elements / history / notice ------------------------------------------

    [Fact]
    public async Task Numbered_elements_render_into_the_user_content_by_index_role_name_and_bounds()
    {
        var elements = new[]
        {
            new UiElement(1, "button", "OK", 40, 50, 20, 20),
            new UiElement(2, "textbox", "Search", 5, 6, 100, 24),
        };
        var handler = await CaptureRequestAsync(Context(elements: elements));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        content.ShouldContain("Numbered elements");
        content.ShouldContain("""[1] button "OK" @ (40,50,20,20)""");
        content.ShouldContain("""[2] textbox "Search" @ (5,6,100,24)""");
    }

    [Fact]
    public async Task Recent_history_steps_render_with_ok_and_failed_status()
    {
        var history = new[]
        {
            new AgentStep(new AgentAction { Type = AgentActionType.Click, X = 1, Y = 2 }, true, "hit OK"),
            new AgentStep(new AgentAction { Type = AgentActionType.Type, Text = "hi" }, false, "field not focused"),
        };
        var handler = await CaptureRequestAsync(Context(history: history));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        content.ShouldContain("Recent steps");
        content.ShouldContain("Click -> ok: hit OK");
        content.ShouldContain("Type -> FAILED: field not focused");
    }

    [Fact]
    public async Task Notice_is_rendered_into_the_user_content_when_set()
    {
        var handler = await CaptureRequestAsync(
            Context(notice: "your last output was not valid JSON"));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        content.ShouldContain("IMPORTANT:");
        content.ShouldContain("your last output was not valid JSON");
    }

    [Fact]
    public async Task Omits_element_history_and_notice_sections_when_context_has_none()
    {
        var handler = await CaptureRequestAsync(Context());

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        content.ShouldNotContain("Numbered elements");
        content.ShouldNotContain("Recent steps");
        content.ShouldNotContain("IMPORTANT:");
    }
}
