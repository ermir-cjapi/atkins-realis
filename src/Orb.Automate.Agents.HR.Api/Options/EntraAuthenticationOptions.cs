using System.ComponentModel.DataAnnotations;

namespace Orb.Automate.Agents.HR.Api.Options;

/// <summary>
/// Configuration for validating Microsoft Entra ID access tokens presented to the API.
/// </summary>
public sealed class EntraAuthenticationOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "EntraAuthentication";

    /// <summary>
    /// Gets the tenant that issues accepted access tokens.
    /// </summary>
    [Required]
    public string TenantId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the API application ID URI or client ID expected in the access-token audience.
    /// </summary>
    [Required]
    public string Audience { get; init; } = string.Empty;

    /// <summary>
    /// Gets the delegated scope required to invoke chat routes.
    /// </summary>
    [Required]
    public string RequiredScope { get; init; } = string.Empty;
}