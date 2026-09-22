namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>Metadata needed to recognise and emit a ServiceNow citation.</summary>
/// <param name="FileId">The ServiceNow record identifier.</param>
/// <param name="FileName">The record title used as a link label.</param>
/// <param name="KbNumber">The knowledge-base number, when present.</param>
/// <param name="SitePath">The controlled ServiceNow source path.</param>
/// <param name="FreshnessFlag">Either <c>Current</c> or <c>Stale</c>.</param>
public sealed record ServiceNowCitationSource(
    string FileId,
    string FileName,
    string KbNumber,
    string SitePath,
    string FreshnessFlag);

/// <summary>
/// Bounded facts captured for one grounding tool invocation. No query text, document content, URL,
/// file identity or exception detail is retained.
/// </summary>
/// <param name="Tool">The bounded tool identifier.</param>
/// <param name="InvocationMode">Either <c>orchestrated</c> or <c>model_controlled</c>.</param>
/// <param name="Status">The controlled tool status.</param>
/// <param name="FailureKind">The controlled failure kind, or <c>none</c>.</param>
/// <param name="Duration">The invocation duration.</param>
/// <param name="RetrievedCount">The number of results retrieved from the source.</param>
/// <param name="ReturnedCount">The number of results returned to the model.</param>
/// <param name="FilteredCount">The number of retrieved results removed by policy or budget.</param>
/// <param name="Freshness">The controlled current or stale state.</param>
/// <param name="CacheState">The controlled cache state.</param>
public sealed record GroundingToolObservation(
    string Tool,
    string InvocationMode,
    string Status,
    string FailureKind,
    TimeSpan Duration,
    int RetrievedCount,
    int ReturnedCount,
    int FilteredCount,
    string Freshness,
    string CacheState);

/// <summary>Immutable bounded grounding aggregate for one turn.</summary>
/// <param name="Invocations">Every grounding tool invocation in the turn.</param>
/// <param name="RetrievedCount">The total retrieved result count.</param>
/// <param name="ReturnedCount">The total count returned to the model.</param>
/// <param name="CitableSourceCount">The number of distinct source documents offered to the model.</param>
/// <param name="CitedSourceCount">The number of distinct source documents cited by the answer.</param>
/// <param name="StaleSourceCount">The number of distinct citable documents flagged stale.</param>
/// <param name="CitationValidationOutcome">Either <c>not_required</c>, <c>passed</c> or <c>failed</c>.</param>
/// <param name="CitationCoverage">Cited over citable, or <see langword="null"/> when nothing was citable.</param>
/// <param name="SafeFallbackApplied">Whether the safe-fallback answer replaced the model output.</param>
/// <param name="RetrievedSourceSystems">The source systems that returned results in the turn.</param>
public sealed record GroundingTelemetrySnapshot(
    IReadOnlyList<GroundingToolObservation> Invocations,
    int RetrievedCount,
    int ReturnedCount,
    int CitableSourceCount,
    int CitedSourceCount,
    int StaleSourceCount,
    string CitationValidationOutcome,
    double? CitationCoverage,
    bool SafeFallbackApplied,
    IReadOnlyList<string> RetrievedSourceSystems)
{
    /// <summary>Gets a snapshot describing a turn in which no grounding was attempted.</summary>
    public static GroundingTelemetrySnapshot Empty { get; } =
        new([], 0, 0, 0, 0, 0, CitationValidationOutcomes.NotRequired, null, false, []);
}

/// <summary>Controlled citation-validation outcomes.</summary>
public static class CitationValidationOutcomes
{
    /// <summary>No approved grounding was returned, so citation validation did not apply.</summary>
    public const string NotRequired = "not_required";

    /// <summary>Grounding required citation and the answer cited an approved source.</summary>
    public const string Passed = "passed";

    /// <summary>Grounding required citation and the answer did not cite an approved source.</summary>
    public const string Failed = "failed";
}

/// <summary>Bounded source-system identifiers used by grounding telemetry and metrics.</summary>
public static class GroundingSources
{
    /// <summary>The SharePoint knowledge index.</summary>
    public const string SharePoint = "sharepoint";

    /// <summary>The ServiceNow AI Search knowledge and catalogue content.</summary>
    public const string ServiceNow = "servicenow";
}

/// <summary>Bounded grounding tool identifiers.</summary>
public static class GroundingTools
{
    /// <summary>The SharePoint HR policy document search tool.</summary>
    public const string SharePointSearch = "sharepoint_search";

    /// <summary>The ServiceNow scoped knowledge article search tool.</summary>
    public const string ServiceNowSearch = "servicenow_search";
}
