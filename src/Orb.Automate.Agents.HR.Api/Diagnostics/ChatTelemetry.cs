using Orb.Automate.Agents.HR.Api.Contracts;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>Properties emitted with an <c>AgentResponseSent</c> event.</summary>
/// <param name="NormalizedIntent">The controlled intent label.</param>
/// <param name="IntentExemplar">The canonical exemplar question.</param>
/// <param name="Topic">The taxonomy topic.</param>
/// <param name="TaxonomyVersion">The taxonomy version.</param>
/// <param name="AnswerConfidence">The classifier confidence.</param>
/// <param name="GroundingSource">The citation-derived grounding classification.</param>
/// <param name="CitationCount">The number of emitted citations.</param>
/// <param name="LatencyMs">The total response latency, retained for schema v1 compatibility.</param>
/// <param name="PromptTokens">Main-agent input tokens, or null when the provider reported none.</param>
/// <param name="CompletionTokens">Main-agent output tokens, or null when the provider reported none.</param>
/// <param name="ContentFiltered">Whether the provider finish reason was a content filter.</param>
/// <param name="ResponseMode">Either <c>complete</c> or <c>streaming</c>.</param>
/// <param name="ResponseOutcome">The controlled response outcome.</param>
/// <param name="ProviderResponseId">The final provider response identifier for the answer.</param>
/// <param name="ClassificationOutcome">The controlled classifier outcome.</param>
/// <param name="Ai">The per-turn provider accounting snapshot.</param>
/// <param name="Grounding">The per-turn bounded grounding snapshot.</param>
/// <param name="GroundingDurationMs">API-orchestrated retrieval duration.</param>
/// <param name="ModelDurationMs">Logical model-generation duration including tool-loop rounds.</param>
/// <param name="ClassificationDurationMs">Classification duration.</param>
/// <param name="TotalTurnDurationMs">Total service-turn duration.</param>
/// <param name="StreamingOutputBuffered">Whether citation policy buffered streamed output.</param>
public sealed record AgentResponseTelemetry(
    string NormalizedIntent,
    string IntentExemplar,
    string Topic,
    string TaxonomyVersion,
    double AnswerConfidence,
    string GroundingSource,
    int CitationCount,
    long LatencyMs,
    long? PromptTokens,
    long? CompletionTokens,
    bool ContentFiltered,
    string ResponseMode,
    string ResponseOutcome,
    string? ProviderResponseId,
    string ClassificationOutcome,
    AiTurnSnapshot Ai,
    GroundingTelemetrySnapshot Grounding,
    long GroundingDurationMs,
    long ModelDurationMs,
    long ClassificationDurationMs,
    long TotalTurnDurationMs,
    bool StreamingOutputBuffered);

/// <summary>Emits Assist | HR chat business events.</summary>
public interface IChatTelemetry
{
    /// <summary>Emits the events known when a user turn arrives.</summary>
    void UserTurnReceived(ChatTurn turn);

    /// <summary>Emits a completed response and its per-source citation facts.</summary>
    void AgentResponseSent(ChatTurn turn, AgentResponseTelemetry response, IReadOnlyList<ChatCitation> citations);

    /// <summary>Emits conversation closure when the client supplies closure metadata.</summary>
    void ConversationClosed(ChatTurn turn);
}

/// <summary>Emits structured OpenTelemetry logs mapped by Azure Monitor to custom events.</summary>
public sealed class ChatTelemetry(ILogger<ChatTelemetry> logger) : IChatTelemetry
{
    private const string CustomEventName = "microsoft.custom_event.name";

    /// <inheritdoc />
    public void UserTurnReceived(ChatTurn turn)
    {
        if (turn.IsConversationStart)
        {
            Emit("ConversationStarted", turn.Telemetry, []);
        }

        Emit("UserMessageReceived", turn.Telemetry,
        [
            new("messageIndex", turn.MessageIndex),
            new("messageLength", turn.Message.Length)
        ]);
    }

    /// <inheritdoc />
    public void AgentResponseSent(ChatTurn turn, AgentResponseTelemetry response, IReadOnlyList<ChatCitation> citations)
    {
        Emit("AgentResponseSent", turn.Telemetry,
        [
            new("schemaVersion", 2),
            new("messageIndex", turn.MessageIndex),
            new("normalizedIntent", response.NormalizedIntent),
            new("intentExemplar", response.IntentExemplar),
            new("topic", response.Topic),
            new("taxonomyVersion", response.TaxonomyVersion),
            new("answerConfidence", response.AnswerConfidence),
            new("groundingSource", response.GroundingSource),
            new("citationCount", response.CitationCount),
            new("latencyMs", response.LatencyMs),
            new("promptTokens", response.PromptTokens),
            new("completionTokens", response.CompletionTokens),
            new("contentFiltered", response.ContentFiltered),
            new("modelDeployment", turn.Telemetry.ModelDeployment),
            new("responseMode", response.ResponseMode),
            new("responseOutcome", response.ResponseOutcome),
            new("successfulResponse", response.ResponseOutcome is "success" or "safe_fallback"),
            new("classificationOutcome", response.ClassificationOutcome),
            new("providerResponseId", response.ProviderResponseId),
            new("mainInputTokens", response.Ai.MainAgent.Usage.InputTokens),
            new("mainOutputTokens", response.Ai.MainAgent.Usage.OutputTokens),
            new("mainTotalTokens", response.Ai.MainAgent.Usage.TotalTokens),
            new("mainCachedInputTokens", response.Ai.MainAgent.Usage.CachedInputTokens),
            new("mainReasoningTokens", response.Ai.MainAgent.Usage.ReasoningTokens),
            new("mainUsageAvailable", response.Ai.MainAgent.Usage.Available),
            new("mainUsageComplete", response.Ai.MainAgent.UsageComplete),
            new("classifierInputTokens", response.Ai.Classifier.Usage.InputTokens),
            new("classifierOutputTokens", response.Ai.Classifier.Usage.OutputTokens),
            new("classifierTotalTokens", response.Ai.Classifier.Usage.TotalTokens),
            new("classifierCachedInputTokens", response.Ai.Classifier.Usage.CachedInputTokens),
            new("classifierReasoningTokens", response.Ai.Classifier.Usage.ReasoningTokens),
            new("classifierUsageAvailable", response.Ai.Classifier.Usage.Available),
            new("classifierUsageComplete", response.Ai.Classifier.UsageComplete),
            new("combinedInputTokens", response.Ai.CombinedUsage.InputTokens),
            new("combinedOutputTokens", response.Ai.CombinedUsage.OutputTokens),
            new("combinedTotalTokens", response.Ai.CombinedUsage.TotalTokens),
            new("combinedCachedInputTokens", response.Ai.CombinedUsage.CachedInputTokens),
            new("combinedReasoningTokens", response.Ai.CombinedUsage.ReasoningTokens),
            new("combinedUsageAvailable", response.Ai.CombinedUsage.Available),
            new("combinedUsageComplete", response.Ai.CombinedUsageComplete),
            new("mainModelCallCount", response.Ai.MainAgent.CallCount),
            new("classifierModelCallCount", response.Ai.Classifier.CallCount),
            new("finalMainModelId", response.Ai.MainAgent.FinalModelId),
            new("finalClassifierModelId", response.Ai.Classifier.FinalModelId),
            new("distinctActualModelIds", string.Join(",", response.Ai.DistinctModelIds)),
            new("multipleModelsObserved", response.Ai.MultipleModelsObserved),
            new("groundingDurationMs", response.GroundingDurationMs),
            new("modelDurationMs", response.ModelDurationMs),
            new("classificationDurationMs", response.ClassificationDurationMs),
            new("totalTurnDurationMs", response.TotalTurnDurationMs),
            new("providerTtftMs", response.Ai.MainAgent.StreamingTimeToFirstText?.TotalMilliseconds),
            new("streamingOutputBuffered", response.StreamingOutputBuffered),
            new("toolInvocationCount", response.Grounding.Invocations.Count),
            new("sharePointToolInvocationCount",
                response.Grounding.Invocations.Count(item => item.Tool == GroundingTools.SharePointSearch)),
            new("serviceNowToolInvocationCount",
                response.Grounding.Invocations.Count(item => item.Tool == GroundingTools.ServiceNowSearch)),
            new("retrievedResultCount", response.Grounding.RetrievedCount),
            new("returnedResultCount", response.Grounding.ReturnedCount),
            new("filteredResultCount", response.Grounding.Invocations.Sum(item => item.FilteredCount)),
            new("retrievedSourceSystems", string.Join(",", response.Grounding.RetrievedSourceSystems)),
            new("citationValidationOutcome", response.Grounding.CitationValidationOutcome),
            new("citableSourceCount", response.Grounding.CitableSourceCount),
            new("citedSourceCount", response.Grounding.CitedSourceCount),
            new("staleSourceCount", response.Grounding.StaleSourceCount),
            new("citationCoverage", response.Grounding.CitationCoverage),
            new("safeFallbackApplied", response.Grounding.SafeFallbackApplied)
        ]);

        foreach (var citation in citations)
        {
            Emit("CitationEmitted", turn.Telemetry,
            [
                new("fileId", citation.ParentId),
                new("fileName", citation.Title),
                new("sourceSystem", citation.SourceSystem),
                new("sitePath", citation.SitePath),
                new("kbNumber", citation.KbNumber),
                new("freshnessFlag", citation.FreshnessFlag)
            ]);
        }
    }

    /// <inheritdoc />
    public void ConversationClosed(ChatTurn turn)
    {
        if (turn.Close is not { } close)
        {
            return;
        }

        Emit("ConversationClosed", turn.Telemetry,
        [
            new("conversationDurationSeconds", close.ConversationDurationSeconds),
            new("turnCount", close.TurnCount),
            new("closeReason", close.CloseReason),
            new("resolved", close.Resolved)
        ]);
    }

    private void Emit(string eventName, ChatTelemetryContext context, IReadOnlyList<KeyValuePair<string, object?>> properties)
    {
        var state = new List<KeyValuePair<string, object?>>(properties.Count + 14)
        {
            new(CustomEventName, eventName),
            new("conversationId", context.ConversationId),
            new("activityId", context.ActivityId),
            new("correlationId", context.CorrelationId),
            new("userId", context.UserId),
            new("userCountry", context.UserCountry),
            new("userDepartment", context.UserDepartment),
            new("userCompany", context.UserCompany),
            new("userDivision", context.UserDivision),
            new("userCostCenter", context.UserCostCenter),
            new("channel", context.Channel),
            new("environment", context.Environment),
            new("agentVersion", context.AgentVersion),
            new("{OriginalFormat}", eventName)
        };
        state.AddRange(properties);
        logger.Log(LogLevel.Information, default, state, null,
            static (values, _) => values[0].Value?.ToString() ?? string.Empty);
    }
}
