using System.Net;
using System.Text;
using Agents.Desktop;

namespace Agents.Tests;

/// <summary>
/// Records every <see cref="IProcessRunner"/> invocation (file + argv + stdin) so driver tests can
/// assert the exact command constructed, without spawning a real process. Returns a configurable
/// <see cref="ProcessResult"/> (exit 0, empty output by default). SHARED across driver test files.
/// </summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly Func<string, IReadOnlyList<string>, ProcessResult>? _responder;

    public RecordingProcessRunner(Func<string, IReadOnlyList<string>, ProcessResult>? responder = null)
        => _responder = responder;

    /// <summary>All recorded invocations, in order.</summary>
    public List<ProcessCall> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(
        string file, IReadOnlyList<string> args, byte[]? stdin = null, CancellationToken ct = default)
    {
        Calls.Add(new ProcessCall(file, [.. args], stdin));
        var result = _responder?.Invoke(file, args) ?? new ProcessResult(0, [], string.Empty);
        return Task.FromResult(result);
    }
}

/// <summary>One recorded process invocation.</summary>
internal sealed record ProcessCall(string File, IReadOnlyList<string> Args, byte[]? Stdin)
{
    /// <summary>The command rendered as "file arg1 arg2 …" for convenient assertions.</summary>
    public string CommandLine => $"{File} {string.Join(' ', Args)}";
}

/// <summary>
/// Test <see cref="HttpMessageHandler"/> that captures the outgoing request (URL + body) and returns
/// a canned response, so <c>OllamaProvider</c> is exercised without a live Ollama server. SHARED
/// across provider test files.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _responder;
    private readonly HttpResponseMessage? _canned;

    /// <summary>Always reply with <paramref name="responseJson"/> at <paramref name="status"/>.</summary>
    public StubHttpMessageHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
        => _canned = new HttpResponseMessage(status) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };

    /// <summary>Reply via a custom responder (e.g. to throw, or vary by request).</summary>
    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    /// <summary>The last request seen, for assertions on method/URL/headers.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>The last request body (JSON), for assertions on the payload.</summary>
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return _responder?.Invoke(request) ?? _canned!;
    }
}
