using Orb.Automate.Agents.HR.Api.Agent;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// Captures bounded grounding facts produced during one agent turn by both API-orchestrated and
/// model-controlled retrieval.
/// </summary>
public interface IGroundingTelemetryContext
{
    /// <summary>Starts an isolated grounding-capture scope.</summary>
    /// <returns>A scope that restores the previously active turn when disposed.</returns>
    IDisposable BeginTurn();

    /// <summary>Marks the retrieval invocation mode for the current async flow.</summary>
    /// <param name="mode">Either <c>orchestrated</c> or <c>model_controlled</c>.</param>
    /// <returns>A scope that restores the previous mode when disposed.</returns>
    IDisposable BeginInvocationMode(string mode);

    /// <summary>Records a completed SharePoint retrieval.</summary>
    /// <param name="result">The bounded tool result.</param>
    /// <param name="duration">The invocation duration.</param>
    void RecordSharePoint(SearchHrPolicyDocumentsResult result, TimeSpan duration);

    /// <summary>Records a completed ServiceNow retrieval.</summary>
    /// <param name="result">The bounded tool result.</param>
    /// <param name="duration">The invocation duration.</param>
    void RecordServiceNow(GetScopedKnowledgeArticlesResult result, TimeSpan duration);

    /// <summary>Records the derived citation-validation facts for the turn.</summary>
    /// <param name="outcome">A value from <see cref="CitationValidationOutcomes"/>.</param>
    /// <param name="citableSourceCount">Distinct source documents offered to the model.</param>
    /// <param name="citedSourceCount">Distinct source documents cited by the answer.</param>
    /// <param name="staleSourceCount">Distinct citable documents flagged stale.</param>
    /// <param name="safeFallbackApplied">Whether the safe-fallback answer replaced the model output.</param>
    void RecordCitationValidation(
        string outcome,
        int citableSourceCount,
        int citedSourceCount,
        int staleSourceCount,
        bool safeFallbackApplied);

    /// <summary>Gets the ServiceNow sources made available to the model in the current turn.</summary>
    IReadOnlyList<ServiceNowCitationSource> ServiceNowSources { get; }

    /// <summary>Gets an immutable bounded snapshot of the current turn.</summary>
    GroundingTelemetrySnapshot Snapshot { get; }
}

/// <summary>
/// Async-flow-local implementation safe for singleton tool registrations and for concurrent or
/// nested turns.
/// </summary>
/// <param name="metrics">The bounded chat and AI metrics sink.</param>
public sealed class GroundingTelemetryContext(IChatAiMetrics metrics) : IGroundingTelemetryContext
{
    private const string ModelControlled = "model_controlled";

    private readonly AsyncLocal<State?> current = new();
    private readonly AsyncLocal<string?> invocationMode = new();

    /// <inheritdoc />
    public IReadOnlyList<ServiceNowCitationSource> ServiceNowSources => current.Value?.Sources ?? [];

    /// <inheritdoc />
    public GroundingTelemetrySnapshot Snapshot
    {
        get
        {
            if (current.Value is not { } state)
            {
                return GroundingTelemetrySnapshot.Empty;
            }

            lock (state.Gate)
            {
                var invocations = state.Invocations.ToArray();
                return new(
                    invocations,
                    invocations.Sum(item => item.RetrievedCount),
                    invocations.Sum(item => item.ReturnedCount),
                    state.CitableSourceCount,
                    state.CitedSourceCount,
                    state.StaleSourceCount,
                    state.ValidationOutcome,
                    Coverage(state.CitableSourceCount, state.CitedSourceCount),
                    state.SafeFallbackApplied,
                    [.. invocations
                        .Where(item => item.ReturnedCount > 0)
                        .Select(SourceSystem)
                        .Distinct(StringComparer.Ordinal)]);
            }
        }
    }

    /// <inheritdoc />
    public IDisposable BeginTurn()
    {
        var previous = current.Value;
        current.Value = new State();
        return new TelemetryScope(() => current.Value = previous);
    }

    /// <inheritdoc />
    public IDisposable BeginInvocationMode(string mode)
    {
        var previous = invocationMode.Value;
        invocationMode.Value = mode;
        return new TelemetryScope(() => invocationMode.Value = previous);
    }

    /// <inheritdoc />
    public void RecordSharePoint(SearchHrPolicyDocumentsResult result, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(result);
        Record(
            GroundingTools.SharePointSearch,
            result.Status,
            duration,
            result.RetrievedCount,
            result.ReturnedCount,
            IsStatus(result.Status, "success") ? "current" : "unavailable",
            "none");
    }

    /// <inheritdoc />
    public void RecordServiceNow(GetScopedKnowledgeArticlesResult result, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(result);
        var stale = IsStatus(result.Status, "stale");
        Record(
            GroundingTools.ServiceNowSearch,
            result.Status,
            duration,
            result.TotalCount,
            result.ReturnedCount,
            stale ? "stale" : "current",
            stale ? "stale" : "fresh");

        if (current.Value is not { } state)
        {
            return;
        }

        lock (state.Gate)
        {
            foreach (var item in result.Results)
            {
                state.Sources.Add(new ServiceNowCitationSource(
                    item.SysId,
                    item.Title,
                    item.Number ?? string.Empty,
                    IsStatus(item.Table, "kb_knowledge") ? "ServiceNow > KB" : "ServiceNow > Catalogue",
                    stale ? "Stale" : "Current"));
            }
        }
    }

    /// <inheritdoc />
    public void RecordCitationValidation(
        string outcome,
        int citableSourceCount,
        int citedSourceCount,
        int staleSourceCount,
        bool safeFallbackApplied)
    {
        if (current.Value is { } state)
        {
            lock (state.Gate)
            {
                state.ValidationOutcome = outcome;
                state.CitableSourceCount = citableSourceCount;
                state.CitedSourceCount = citedSourceCount;
                state.StaleSourceCount = staleSourceCount;
                state.SafeFallbackApplied = safeFallbackApplied;
            }
        }

        metrics.RecordCitation(outcome, Coverage(citableSourceCount, citedSourceCount));
    }

    private static double? Coverage(int citable, int cited) => citable > 0 ? (double)cited / citable : null;

    private static string SourceSystem(GroundingToolObservation observation) =>
        observation.Tool == GroundingTools.SharePointSearch ? GroundingSources.SharePoint : GroundingSources.ServiceNow;

    private static bool IsStatus(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private void Record(
        string tool,
        string status,
        TimeSpan duration,
        int retrieved,
        int returned,
        string freshness,
        string cacheState)
    {
        var failureKind = status switch
        {
            "invalid_query" => "invalid_request",
            "identity_unavailable" => "identity_unavailable",
            "unavailable" => "provider_unavailable",
            _ => "none"
        };
        var outcome = status is "success" or "stale" or "no_results" ? "succeeded" : "failed";
        var mode = invocationMode.Value ?? ModelControlled;
        var observation = new GroundingToolObservation(
            tool, mode, status, failureKind, duration,
            retrieved, returned, Math.Max(0, retrieved - returned), freshness, cacheState);

        if (current.Value is { } state)
        {
            lock (state.Gate)
            {
                state.Invocations.Add(observation);
            }
        }

        metrics.RecordTool(tool, mode, outcome, failureKind, duration);
        metrics.RecordGrounding(duration, SourceSystem(observation), retrieved, returned);
    }

    private sealed class State
    {
        public object Gate { get; } = new();

        public List<GroundingToolObservation> Invocations { get; } = [];

        public List<ServiceNowCitationSource> Sources { get; } = [];

        public string ValidationOutcome { get; set; } = CitationValidationOutcomes.NotRequired;

        public int CitableSourceCount { get; set; }

        public int CitedSourceCount { get; set; }

        public int StaleSourceCount { get; set; }

        public bool SafeFallbackApplied { get; set; }
    }
}
