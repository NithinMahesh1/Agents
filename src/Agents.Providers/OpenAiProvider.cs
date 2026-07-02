using Agents.Core;

namespace Agents.Providers;

/// <summary>
/// Phase 2 stub for an OpenAI-backed <see cref="IModelProvider"/>.
/// Not yet implemented; <see cref="DecideAsync"/> throws.
/// </summary>
// TODO (phase 2): drive the model via the official OpenAI SDK (computer-use / responses API).
// Do NOT add the SDK package before then.
public sealed class OpenAiProvider : IModelProvider
{
    /// <inheritdoc />
    public string Name => "openai";

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentAction>> DecideAsync(AgentContext context, CancellationToken ct = default) =>
        throw new NotImplementedException("OpenAiProvider is planned for phase 2.");
}
