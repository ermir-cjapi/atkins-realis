using Orb.Automate.Agents.HR.Api.Contracts;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Provides chat operations backed by the configured AI agent.
/// </summary>
public interface IAgentChatService
{
    /// <summary>
    /// Gets a complete response for a single chat turn.
    /// </summary>
    /// <param name="turn">The validated and telemetry-enriched user turn.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// The completed chat response, including an <see cref="EmailDraft"/> when the agent
    /// chose to draft an email.
    /// </returns>
    Task<ChatResponse> GetResponseAsync(ChatTurn turn, CancellationToken cancellationToken);

    /// <summary>
    /// Streams incremental response updates for a single chat turn.
    /// </summary>
    /// <param name="turn">The validated and telemetry-enriched user turn.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// An async sequence of streamed chat updates. Chunks are emitted incrementally unless
    /// citation validation or email-draft extraction requires the completed response, in which
    /// case a single final update is returned. On the incremental path, text is held back from
    /// the first <c>```</c> so an <c>email-draft</c> fence is not leaked to the client. When the
    /// agent produced an email draft, the final update includes a structured
    /// <see cref="EmailDraft"/> and the visible text has the fence removed.
    /// </returns>
    IAsyncEnumerable<ChatStreamUpdate> StreamResponseAsync(ChatTurn turn, CancellationToken cancellationToken);
}
