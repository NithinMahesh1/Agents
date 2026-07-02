using Agents.Core;

namespace Agents.Providers;

/// <summary>
/// Phase 2 stub for an Anthropic Claude-backed <see cref="IModelProvider"/>.
/// Not yet implemented; <see cref="DecideAsync"/> throws.
/// </summary>
// TODO (phase 2): drive Claude via the official `Anthropic` C# SDK computer-use tool loop.
// Confirm the exact tool type (e.g. computer_20xx-xx-xx) and the required anthropic-beta
// header from the live docs at implementation time. Do NOT add the SDK package before then.
public sealed class ClaudeProvider : IModelProvider
{
    /// <inheritdoc />
    public string Name => "claude";

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentAction>> DecideAsync(AgentContext context, CancellationToken ct = default) =>
        throw new NotImplementedException("ClaudeProvider is planned for phase 2.");
}
