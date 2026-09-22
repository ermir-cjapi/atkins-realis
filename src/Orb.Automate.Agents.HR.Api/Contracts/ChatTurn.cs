namespace Orb.Automate.Agents.HR.Api.Contracts;

/// <summary>A validated chat turn enriched with dimensions shared by all business events.</summary>
public sealed record ChatTurn(
    string Message,
    string ConversationId,
    string ActivityId,
    int MessageIndex,
    bool IsConversationStart,
    ConversationClose? Close,
    ChatTelemetryContext Telemetry);

/// <summary>Dimensions shared by every ARIA business event.</summary>
public sealed record ChatTelemetryContext(
    string ConversationId,
    string ActivityId,
    string CorrelationId,
    string UserId,
    string UserCountry,
    string UserDepartment,
    string Channel,
    string Environment,
    string AgentVersion,
    string ModelDeployment);