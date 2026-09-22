namespace Orb.Automate.Agents.HR.Api.Contracts;

/// <summary>
/// Represents a chat request submitted by a client.
/// </summary>
/// <param name="Message">The user message to send to the agent.</param>
/// <param name="ConversationId">The stable Teams conversation identifier.</param>
/// <param name="ActivityId">The Bot Framework activity identifier for this turn.</param>
/// <param name="MessageIndex">The one-based user-message index within the conversation.</param>
/// <param name="IsConversationStart">Whether this is the first user turn in the conversation.</param>
/// <param name="Close">Optional client-owned conversation closure metadata.</param>
public sealed record ChatRequest(
    string? Message,
    string? ConversationId = null,
    string? ActivityId = null,
    int? MessageIndex = null,
    bool IsConversationStart = false,
    ConversationClose? Close = null);

/// <summary>Client-owned metadata supplied when a conversation closes.</summary>
/// <param name="ConversationDurationSeconds">Elapsed conversation duration in seconds.</param>
/// <param name="TurnCount">The number of user turns in the conversation.</param>
/// <param name="CloseReason">The controlled reason the conversation closed.</param>
/// <param name="Resolved">Whether the user's question was resolved, or null when unanswered.</param>
public sealed record ConversationClose(
    int ConversationDurationSeconds,
    int TurnCount,
    string CloseReason,
    bool? Resolved = null);
