using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agents.Core;

namespace Agents.Providers;

/// <summary>
/// <see cref="IModelProvider"/> backed by a local <c>Ollama</c> server's <c>/api/chat</c>
/// endpoint. Sends the current screenshot (as a base64 image) plus a textual context and
/// asks a vision model (default <c>qwen2.5-vl</c>) for the next desktop action(s).
/// </summary>
/// <remarks>
/// The provider never contacts a live server on construction; all I/O happens in
/// <see cref="DecideAsync"/>. An <see cref="HttpClient"/> (or its backing
/// <see cref="HttpMessageHandler"/>) may be injected to make the type unit-testable
/// without a running Ollama instance.
/// </remarks>
public sealed class OllamaProvider : IModelProvider, IDisposable
{
    private const string DefaultBaseUrl = "http://localhost:11434";

    private static readonly string SystemPrompt =
        "You control a desktop through a screenshot; choose the next action(s)."
        + "\n\n"
        + AgentActionParser.SchemaPrompt;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _model;
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>Create a provider targeting an Ollama server.</summary>
    /// <param name="model">Vision model tag to run, e.g. <c>qwen2.5-vl</c>.</param>
    /// <param name="baseUrl">Server base URL; defaults to <c>http://localhost:11434</c>.</param>
    /// <param name="httpClient">
    /// Optional client to reuse (the unit-test seam). When <see langword="null"/> a client is
    /// created and owned by this instance; when supplied it is used as-is and never disposed here.
    /// </param>
    public OllamaProvider(string model = "qwen2.5-vl", string? baseUrl = null, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model must be a non-empty string.", nameof(model));

        _model = model;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');

        if (httpClient is null)
        {
            _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    /// <inheritdoc />
    public string Name => $"ollama:{_model}";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentAction>> DecideAsync(AgentContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = BuildRequest(context);

        // Always use an absolute URL so the call works whether or not an injected client
        // (the test seam) has a BaseAddress configured. Genuine transport failures
        // (connection refused, DNS, timeout) surface as HttpRequestException and propagate.
        using var response = await _http
            .PostAsJsonAsync($"{_baseUrl}/api/chat", request, JsonOptions, ct)
            .ConfigureAwait(false);

        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        string? content = null;
        try
        {
            content = JsonSerializer.Deserialize<ChatResponse>(raw, JsonOptions)?.Message?.Content;
        }
        catch (JsonException)
        {
            // Malformed envelope — treated as "no usable content" below rather than thrown.
        }

        // No usable content, or output that parses to nothing, returns an EMPTY list: AgentLoop then
        // feeds a corrective Notice back to the model and retries (up to MaxConsecutiveEmpty). A real
        // "task cannot be done" Fail must be decided by the model, never synthesized here.
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return AgentActionParser.Parse(content);
    }

    /// <summary>Dispose the owned <see cref="HttpClient"/> (no-op when one was injected).</summary>
    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private ChatRequest BuildRequest(AgentContext context)
    {
        var image = Convert.ToBase64String(context.Screen.PngBytes);

        var messages = new[]
        {
            new ChatMessage("system", SystemPrompt),
            new ChatMessage("user", BuildUserText(context), new[] { image }),
        };

        return new ChatRequest(_model, Stream: false, Format: "json", messages);
    }

    private static string BuildUserText(AgentContext context)
    {
        var sb = new StringBuilder();
        sb.Append("Goal: ").Append(context.Goal);

        if (context.Elements.Count > 0)
        {
            sb.Append("\n\nNumbered elements (prefer these by index):");
            foreach (var e in context.Elements)
                sb.Append(CultureInfo.InvariantCulture, $"\n[{e.Index}] {e.Role} \"{e.Name}\" @ ({e.X},{e.Y},{e.Width},{e.Height})");
        }

        if (context.History.Count > 0)
        {
            sb.Append("\n\nRecent steps:");
            foreach (var step in context.History.TakeLast(5))
            {
                var status = step.Ok ? "ok" : "FAILED";
                var detail = string.IsNullOrWhiteSpace(step.Detail) ? string.Empty : $": {step.Detail}";
                sb.Append(CultureInfo.InvariantCulture, $"\n- {step.Action.Type} -> {status}{detail}");
            }
        }

        // Corrective feedback from the loop (e.g. the last output was unparseable) — placed last so it is salient.
        if (!string.IsNullOrWhiteSpace(context.Notice))
            sb.Append("\n\nIMPORTANT: ").Append(context.Notice);

        return sb.ToString();
    }

    // --- Ollama /api/chat wire contract (BCL System.Text.Json only) ---

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("images")] IReadOnlyList<string>? Images = null);

    private sealed record ChatResponse(
        [property: JsonPropertyName("message")] ResponseMessage? Message);

    private sealed record ResponseMessage(
        [property: JsonPropertyName("content")] string? Content);
}
