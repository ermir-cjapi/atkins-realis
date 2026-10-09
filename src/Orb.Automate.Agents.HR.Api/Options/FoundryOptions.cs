using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;

namespace Orb.Automate.Agents.HR.Api.Options;

/// <summary>
/// Configuration for the Azure AI Foundry connection.
/// </summary>
public sealed class FoundryOptions : IValidatableObject
{
    /// <summary>
    /// The configuration section name for Foundry settings.
    /// </summary>
    public const string SectionName = "Foundry";

    /// <summary>
    /// Gets the HTTPS endpoint for the Foundry resource.
    /// </summary>
    [Required]
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Gets the chat model deployment name.
    /// </summary>
    [Required]
    public string DeploymentName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional reasoning effort level forwarded via the Azure Responses API.
    /// Accepted values (case-insensitive): <c>none</c>, <c>low</c>, <c>medium</c>, <c>high</c>,
    /// <c>xhigh</c> and its compatibility alias <c>extrahigh</c>.
    /// Leave empty to use the model's default reasoning effort.
    /// <para>
    /// Increasing the effort level improves response quality at the cost of higher latency and
    /// token consumption. <c>low</c> is a good starting point for most HR queries.
    /// </para>
    /// <para>
    /// Provider support is model-dependent. <c>max</c> is rejected because the installed
    /// Microsoft.Extensions.AI adapter cannot represent that effort; it is never silently omitted.
    /// </para>
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Validates reasoning names without accepting numeric enum values.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(ReasoningEffort) && ParseReasoningEffort() is null)
        {
            yield return new ValidationResult(
                "Foundry:ReasoningEffort must be none, low, medium, high, xhigh or extrahigh. " +
                "The installed adapter does not support max.", [nameof(ReasoningEffort)]);
        }
    }

    /// <summary>Creates provider-neutral reasoning options after startup validation.</summary>
    public ReasoningOptions? CreateReasoningOptions() => string.IsNullOrWhiteSpace(ReasoningEffort)
        ? null
        : new ReasoningOptions
        {
            Effort = ParseReasoningEffort() ?? throw new ValidationException("Invalid Foundry:ReasoningEffort.")
        };

    private Microsoft.Extensions.AI.ReasoningEffort? ParseReasoningEffort() =>
        ReasoningEffort?.Trim().ToLowerInvariant() switch
        {
            "none" => Microsoft.Extensions.AI.ReasoningEffort.None,
            "low" => Microsoft.Extensions.AI.ReasoningEffort.Low,
            "medium" => Microsoft.Extensions.AI.ReasoningEffort.Medium,
            "high" => Microsoft.Extensions.AI.ReasoningEffort.High,
            "xhigh" or "extrahigh" => Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh,
            _ => null
        };
}
