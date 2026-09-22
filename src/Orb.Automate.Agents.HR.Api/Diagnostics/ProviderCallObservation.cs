namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>Controlled outcome of one physical provider call.</summary>
public enum ProviderCallOutcome
{
    /// <summary>The provider call completed and, for streaming, was enumerated to completion.</summary>
    Succeeded,

    /// <summary>The provider call failed.</summary>
    Failed,

    /// <summary>The provider call was cancelled.</summary>
    Cancelled,

    /// <summary>A streaming call was disposed before its enumeration completed.</summary>
    Abandoned
}

/// <summary>One immutable observation of a physical provider request.</summary>
/// <param name="Sequence">The per-turn provider-call sequence number.</param>
/// <param name="Component">The bounded component that issued the call.</param>
/// <param name="Streaming">Whether the call used the streaming API.</param>
/// <param name="Usage">The token usage reported by the provider.</param>
/// <param name="ModelId">The actual model identifier returned by the provider.</param>
/// <param name="ResponseId">The provider response identifier.</param>
/// <param name="FinishReason">The provider finish reason.</param>
/// <param name="Duration">The wall-clock duration of the call.</param>
/// <param name="TimeToFirstText">Streaming time until the first non-empty text update.</param>
/// <param name="Outcome">The controlled call outcome.</param>
public sealed record ProviderCallObservation(
    long Sequence,
    string Component,
    bool Streaming,
    TokenUsageSnapshot Usage,
    string? ModelId,
    string? ResponseId,
    string? FinishReason,
    TimeSpan Duration,
    TimeSpan? TimeToFirstText,
    ProviderCallOutcome Outcome);
