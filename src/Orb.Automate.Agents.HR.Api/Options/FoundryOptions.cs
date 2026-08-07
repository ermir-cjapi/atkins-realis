using System.ComponentModel.DataAnnotations;

namespace Orb.Automate.Agents.HR.Api.Options;

/// <summary>
/// Configuration for the Azure AI Foundry connection.
/// </summary>
public sealed class FoundryOptions
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
}
