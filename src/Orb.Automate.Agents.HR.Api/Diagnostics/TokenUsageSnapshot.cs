using Microsoft.Extensions.AI;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// Immutable token totals. A <see langword="null"/> value means the provider did not report that
/// count; it is never substituted with zero.
/// </summary>
/// <param name="InputTokens">Reported input tokens.</param>
/// <param name="OutputTokens">Reported output tokens.</param>
/// <param name="TotalTokens">Reported total tokens.</param>
/// <param name="CachedInputTokens">Reported cached input tokens.</param>
/// <param name="ReasoningTokens">Reported reasoning tokens.</param>
public sealed record TokenUsageSnapshot(
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long? CachedInputTokens,
    long? ReasoningTokens)
{
    /// <summary>An observation in which the provider reported no usage at all.</summary>
    public static readonly TokenUsageSnapshot None = new(null, null, null, null, null);

    /// <summary>Gets a value indicating whether the provider reported at least one count.</summary>
    public bool Available => InputTokens.HasValue || OutputTokens.HasValue || TotalTokens.HasValue ||
        CachedInputTokens.HasValue || ReasoningTokens.HasValue;

    /// <summary>Creates a snapshot from an optional provider usage payload.</summary>
    /// <param name="usage">The provider usage payload, when supplied.</param>
    /// <returns>The immutable snapshot.</returns>
    public static TokenUsageSnapshot From(UsageDetails? usage) => usage is null
        ? None
        : new(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount,
            usage.CachedInputTokenCount, usage.ReasoningTokenCount);

    /// <summary>
    /// Sums the reported counts. Absent values are skipped rather than treated as zero, so a field
    /// stays <see langword="null"/> only when no contributing observation reported it. Whether every
    /// contributing call reported usage is published separately through
    /// <see cref="AiComponentSnapshot.UsageComplete"/>.
    /// </summary>
    /// <param name="values">The observations to combine.</param>
    /// <returns>The combined snapshot.</returns>
    public static TokenUsageSnapshot Sum(IEnumerable<TokenUsageSnapshot> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        long? input = null, output = null, total = null, cached = null, reasoning = null;
        foreach (var value in values)
        {
            input = Add(input, value.InputTokens);
            output = Add(output, value.OutputTokens);
            total = Add(total, value.TotalTokens);
            cached = Add(cached, value.CachedInputTokens);
            reasoning = Add(reasoning, value.ReasoningTokens);
        }

        return new(input, output, total, cached, reasoning);
    }

    private static long? Add(long? accumulated, long? value) =>
        value.HasValue ? (accumulated ?? 0) + value.Value : accumulated;
}
