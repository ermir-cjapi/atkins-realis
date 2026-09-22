using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// Records bounded chat and AI diagnostics. Only low-cardinality, content-free dimensions are
/// accepted so that no identifier, query, model identity or exception detail becomes a metric tag.
/// </summary>
public interface IChatAiMetrics
{
    /// <summary>Records one physical provider call, its duration and its reported token usage.</summary>
    /// <param name="call">The completed provider-call observation.</param>
    void RecordProviderCall(ProviderCallObservation call);

    /// <summary>Records one attempted service turn and its per-component token totals.</summary>
    /// <param name="duration">The total service-turn duration.</param>
    /// <param name="mode">The bounded response mode, either <c>complete</c> or <c>streaming</c>.</param>
    /// <param name="outcome">The bounded terminal turn outcome.</param>
    /// <param name="configuredDeployment">The configured model deployment name.</param>
    /// <param name="snapshot">The immutable per-turn provider accounting snapshot.</param>
    void RecordTurn(TimeSpan duration, string mode, string outcome, string configuredDeployment, AiTurnSnapshot snapshot);

    /// <summary>Records one grounding retrieval duration and its bounded result counts.</summary>
    /// <param name="duration">The retrieval duration.</param>
    /// <param name="source">The bounded grounding source identifier.</param>
    /// <param name="retrieved">The number of retrieved results.</param>
    /// <param name="returned">The number of results returned to the model.</param>
    void RecordGrounding(TimeSpan duration, string source, int retrieved, int returned);

    /// <summary>Records one grounding tool invocation.</summary>
    /// <param name="tool">The bounded tool identifier.</param>
    /// <param name="invocationMode">Either <c>orchestrated</c> or <c>model_controlled</c>.</param>
    /// <param name="outcome">The bounded controlled outcome.</param>
    /// <param name="failureKind">The bounded controlled failure kind, or <c>none</c>.</param>
    /// <param name="duration">The tool invocation duration.</param>
    void RecordTool(string tool, string invocationMode, string outcome, string failureKind, TimeSpan duration);

    /// <summary>Records the citation-validation outcome and its coverage when coverage is defined.</summary>
    /// <param name="outcome">Either <c>not_required</c>, <c>passed</c> or <c>failed</c>.</param>
    /// <param name="coverage">The cited-to-citable ratio, or <see langword="null"/> when no citable source existed.</param>
    void RecordCitation(string outcome, double? coverage);

    /// <summary>Records one classification outcome.</summary>
    /// <param name="outcome">The bounded classifier outcome.</param>
    void RecordClassification(string outcome);

    /// <summary>Records the latency of the first successfully flushed Server-Sent Event.</summary>
    /// <param name="elapsed">Time from the start of streaming until the first flush completed.</param>
    void RecordSseFirstFlush(TimeSpan elapsed);

    /// <summary>Records the bounded delivery outcome of one Server-Sent Events response.</summary>
    /// <param name="outcome">Either <c>completed</c> or <c>client_aborted</c>.</param>
    void RecordSseOutcome(string outcome);
}

/// <summary>
/// Default <see cref="IChatAiMetrics"/> implementation writing to the
/// <see cref="MeterName"/> OpenTelemetry meter.
/// </summary>
public sealed class ChatAiMetrics : IChatAiMetrics
{
    /// <summary>The OpenTelemetry meter name registered by the service defaults.</summary>
    public const string MeterName = "Orb.Automate.Agents.HR.ChatAI";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Turns = Meter.CreateCounter<long>("chat.turn.count");
    private static readonly Histogram<double> TurnDuration = Meter.CreateHistogram<double>("chat.turn.duration", "ms");
    private static readonly Counter<long> ProviderCalls = Meter.CreateCounter<long>("ai.provider.call.count");
    private static readonly Histogram<double> ProviderDuration = Meter.CreateHistogram<double>("ai.provider.call.duration", "ms");
    private static readonly Histogram<double> ProviderTtft = Meter.CreateHistogram<double>("ai.provider.ttft", "ms");
    private static readonly Counter<long> ProviderTokens = Meter.CreateCounter<long>("ai.provider.token.count");
    private static readonly Counter<long> TurnTokens = Meter.CreateCounter<long>("chat.turn.token.count");
    private static readonly Histogram<double> GroundingDuration = Meter.CreateHistogram<double>("chat.grounding.duration", "ms");
    private static readonly Histogram<long> GroundingResults = Meter.CreateHistogram<long>("chat.grounding.result.count");
    private static readonly Counter<long> ToolInvocations = Meter.CreateCounter<long>("chat.tool.invocation.count");
    private static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>("chat.tool.duration", "ms");
    private static readonly Counter<long> CitationValidations = Meter.CreateCounter<long>("chat.citation.validation.count");
    private static readonly Histogram<double> CitationCoverage = Meter.CreateHistogram<double>("chat.citation.coverage", "ratio");
    private static readonly Counter<long> Classifications = Meter.CreateCounter<long>("chat.classification.count");
    private static readonly Histogram<double> SseFirstFlush = Meter.CreateHistogram<double>("chat.sse.first_flush", "ms");
    private static readonly Counter<long> SseOutcomes = Meter.CreateCounter<long>("chat.sse.outcome.count");

    /// <inheritdoc />
    public void RecordProviderCall(ProviderCallObservation call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var tags = new TagList
        {
            { "component", call.Component },
            { "streaming_mode", call.Streaming ? "streaming" : "complete" },
            { "outcome", call.Outcome.ToString().ToLowerInvariant() }
        };
        ProviderCalls.Add(1, tags);
        ProviderDuration.Record(call.Duration.TotalMilliseconds, tags);
        if (call.TimeToFirstText is { } ttft)
        {
            ProviderTtft.Record(ttft.TotalMilliseconds, tags);
        }

        RecordTokens(ProviderTokens, call.Component, call.Usage);
    }

    /// <inheritdoc />
    public void RecordTurn(TimeSpan duration, string mode, string outcome, string configuredDeployment, AiTurnSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var tags = new TagList
        {
            { "streaming_mode", mode },
            { "outcome", outcome },
            { "configured_deployment", configuredDeployment }
        };
        Turns.Add(1, tags);
        TurnDuration.Record(duration.TotalMilliseconds, tags);

        // Only per-component series are published. The combined turn total is deliberately omitted so
        // that summing chat.turn.token.count without a component filter cannot double count.
        RecordTokens(TurnTokens, snapshot.MainAgent.Component, snapshot.MainAgent.Usage);
        RecordTokens(TurnTokens, snapshot.Classifier.Component, snapshot.Classifier.Usage);
    }

    /// <inheritdoc />
    public void RecordGrounding(TimeSpan duration, string source, int retrieved, int returned)
    {
        GroundingDuration.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("grounding_source", source));
        GroundingResults.Record(retrieved, new TagList { { "grounding_source", source }, { "result_type", "retrieved" } });
        GroundingResults.Record(returned, new TagList { { "grounding_source", source }, { "result_type", "returned" } });
    }

    /// <inheritdoc />
    public void RecordTool(string tool, string invocationMode, string outcome, string failureKind, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "tool", tool },
            { "invocation_mode", invocationMode },
            { "outcome", outcome },
            { "failure_kind", failureKind }
        };
        ToolInvocations.Add(1, tags);
        ToolDuration.Record(duration.TotalMilliseconds, tags);
    }

    /// <inheritdoc />
    public void RecordCitation(string outcome, double? coverage)
    {
        CitationValidations.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        if (coverage.HasValue)
        {
            CitationCoverage.Record(coverage.Value, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <inheritdoc />
    public void RecordClassification(string outcome) =>
        Classifications.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <inheritdoc />
    public void RecordSseFirstFlush(TimeSpan elapsed) => SseFirstFlush.Record(elapsed.TotalMilliseconds);

    /// <inheritdoc />
    public void RecordSseOutcome(string outcome) =>
        SseOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    private static void RecordTokens(Counter<long> instrument, string component, TokenUsageSnapshot usage)
    {
        RecordToken(instrument, component, "input", usage.InputTokens);
        RecordToken(instrument, component, "output", usage.OutputTokens);
        RecordToken(instrument, component, "total", usage.TotalTokens);
        RecordToken(instrument, component, "cached_input", usage.CachedInputTokens);
        RecordToken(instrument, component, "reasoning", usage.ReasoningTokens);
    }

    private static void RecordToken(Counter<long> instrument, string component, string type, long? value)
    {
        if (value.HasValue)
        {
            instrument.Add(value.Value, new TagList { { "component", component }, { "token_type", type } });
        }
    }
}
