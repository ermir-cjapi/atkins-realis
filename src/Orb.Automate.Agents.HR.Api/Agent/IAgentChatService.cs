using Orb.Automate.Agents.HR.Api.Contracts;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Provides chat operations backed by the configured AI agent.
/// </summary>
public interface IAgentChatService
{
    /// <summary>
    /// Gets a complete response for a single chat prompt.
    /// </summary>
    /// <param name="message">The user prompt to send to the agent.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The completed chat response.</returns>
    Task<ChatResponse> GetResponseAsync(string? message, CancellationToken cancellationToken);

    /// <summary>
    /// Streams incremental response updates for a single chat prompt.
    /// </summary>
    /// <param name="message">The user prompt to send to the agent.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>An async sequence of streamed chat updates.</returns>
    IAsyncEnumerable<ChatStreamUpdate> StreamResponseAsync(string? message, CancellationToken cancellationToken);
}
