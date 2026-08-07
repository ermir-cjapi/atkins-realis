using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Orb.Automate.Agents.HR.Api.Contracts;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Default implementation of <see cref="IAgentChatService"/> using an <see cref="AIAgent"/>.
/// </summary>
/// <param name="agent">The configured AI agent.</param>
/// <param name="logger">The logger used for agent invocation events.</param>
public sealed class AgentChatService(AIAgent agent, ILogger<AgentChatService> logger) : IAgentChatService
{
    private static readonly Action<ILogger, Exception?> LogNonStreamingInvocation =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(GetResponseAsync)),
            "Starting non-streaming agent invocation");

    private static readonly Action<ILogger, Exception?> LogStreamingInvocation =
        LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(StreamResponseAsync)),
            "Starting streaming agent invocation");

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(string? message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        LogNonStreamingInvocation(logger, null);
        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);
        return new ChatResponse(response.Text ?? string.Empty, response.ResponseId, response.FinishReason?.ToString());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamUpdate> StreamResponseAsync(
        string? message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        LogStreamingInvocation(logger, null);
        await foreach (var update in agent.RunStreamingAsync(message, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return new ChatStreamUpdate(update.Text, update.ResponseId);
            }
        }
    }
}
