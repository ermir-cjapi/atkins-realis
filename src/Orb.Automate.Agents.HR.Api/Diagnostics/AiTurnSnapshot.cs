namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>Immutable provider accounting for one AI component within a turn.</summary>
/// <param name="Component">The bounded component name.</param>
/// <param name="CallCount">The number of physical provider calls made by the component.</param>
/// <param name="UsageReportingCallCount">The number of those calls that reported any usage.</param>
/// <param name="Usage">The summed token usage across the component's calls.</param>
/// <param name="FinalModelId">The actual model identifier of the component's final call.</param>
/// <param name="FinalResponseId">The provider response identifier of the component's final call.</param>
/// <param name="StreamingTimeToFirstText">The first observed streaming time to first text.</param>
/// <param name="DistinctModelIds">The distinct actual model identifiers observed.</param>
public sealed record AiComponentSnapshot(
    string Component,
    int CallCount,
    int UsageReportingCallCount,
    TokenUsageSnapshot Usage,
    string? FinalModelId,
    string? FinalResponseId,
    TimeSpan? StreamingTimeToFirstText,
    IReadOnlyList<string> DistinctModelIds)
{
    /// <summary>
    /// Gets a value indicating whether every physical call reported usage, so the summed totals
    /// describe the whole component rather than a subset of its calls.
    /// </summary>
    public bool UsageComplete => CallCount > 0 && UsageReportingCallCount == CallCount;

    /// <summary>Creates an empty snapshot for a component that made no calls.</summary>
    /// <param name="component">The bounded component name.</param>
    /// <returns>The empty snapshot.</returns>
    public static AiComponentSnapshot Empty(string component) =>
        new(component, 0, 0, TokenUsageSnapshot.None, null, null, null, []);
}

/// <summary>Immutable provider accounting for one complete service turn.</summary>
/// <param name="MainAgent">Main-agent accounting, including all tool-loop rounds.</param>
/// <param name="Classifier">Intent-classifier accounting.</param>
/// <param name="CombinedUsage">The summed usage across every component.</param>
/// <param name="DistinctModelIds">The distinct actual model identifiers observed in the turn.</param>
/// <param name="MultipleModelsObserved">Whether more than one actual model answered in the turn.</param>
/// <param name="Calls">Every physical provider-call observation, ordered by sequence.</param>
public sealed record AiTurnSnapshot(
    AiComponentSnapshot MainAgent,
    AiComponentSnapshot Classifier,
    TokenUsageSnapshot CombinedUsage,
    IReadOnlyList<string> DistinctModelIds,
    bool MultipleModelsObserved,
    IReadOnlyList<ProviderCallObservation> Calls)
{
    /// <summary>Gets a snapshot describing a turn in which no provider call was observed.</summary>
    public static AiTurnSnapshot Empty { get; } = new(
        AiComponentSnapshot.Empty(AiComponents.MainAgent),
        AiComponentSnapshot.Empty(AiComponents.IntentClassifier),
        TokenUsageSnapshot.None,
        [],
        false,
        []);

    /// <summary>
    /// Gets a value indicating whether every physical call in the turn reported usage.
    /// </summary>
    public bool CombinedUsageComplete => Calls.Count > 0 && Calls.All(call => call.Usage.Available);
}
