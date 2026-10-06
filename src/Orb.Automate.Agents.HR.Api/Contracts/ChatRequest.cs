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
/// <param name="PreviousDraft">
/// An email draft produced in a prior turn. When supplied together with
/// <see cref="RegenerateInstruction"/>, the agent revises the draft instead of generating a new one.
/// </param>
/// <param name="RegenerateInstruction">
/// Free-text instruction describing how to change the previous draft,
/// for example "make it shorter" or "use a more formal tone".
/// Ignored when <see cref="PreviousDraft"/> is not supplied.
/// </param>
/// <param name="AgentContext">
/// The assistant message from the immediately preceding turn, supplied by the client when the
/// conversation is stateless. Allows the model to see its own prior offer without server-side
/// session history — for example, when the user replies "yes please" to an email-draft offer.
/// </param>
public sealed record ChatRequest(
    string? Message,
    string? ConversationId = null,
    string? ActivityId = null,
    int? MessageIndex = null,
    bool IsConversationStart = false,
    ConversationClose? Close = null,
    EmailDraft? PreviousDraft = null,
    string? RegenerateInstruction = null,
    string? AgentContext = null);

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
