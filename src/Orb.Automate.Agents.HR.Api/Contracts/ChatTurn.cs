namespace Orb.Automate.Agents.HR.Api.Contracts;

/// <summary>A validated chat turn enriched with dimensions shared by all business events.</summary>
public sealed record ChatTurn(
    string Message,
    string ConversationId,
    string ActivityId,
    int MessageIndex,
    bool IsConversationStart,
    ConversationClose? Close,
    ChatTelemetryContext Telemetry,
    EmailDraft? PreviousDraft = null,
    string? RegenerateInstruction = null,
    SmartObjective? PreviousObjective = null,
    string? AgentContext = null,
    IReadOnlyList<ConversationExchange>? History = null);

/// <summary>An earlier user message and the reply the user was shown, oldest first in history.</summary>
/// <param name="UserMessage">The user's message text.</param>
/// <param name="AssistantMessage">The validated reply delivered to the user.</param>
public sealed record ConversationExchange(string UserMessage, string AssistantMessage);

/// <summary>Dimensions shared by every Assist | HR business event.</summary>
public sealed record ChatTelemetryContext(
    string ConversationId,
    string ActivityId,
    string CorrelationId,
    string UserId,
    string UserCountry,
    string UserDepartment,
    string UserCompany,
    string UserDivision,
    string UserCostCenter,
    string Channel,
    string Environment,
    string AgentVersion,
    string ModelDeployment);