namespace Orb.Automate.Agents.HR.Api.Options;

/// <summary>
/// Configuration for the Microsoft Teams Bot Connector endpoint.
/// </summary>
public sealed class TeamsOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Teams";

    /// <summary>Gets whether the Bot Connector endpoint is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the client ID of the Azure Bot user-assigned managed identity.</summary>
    public string AppId { get; init; } = string.Empty;

    /// <summary>Gets the Microsoft Entra tenant ID containing the Azure Bot.</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Gets whether delegated Teams SSO is required for message handling.</summary>
    public bool SsoEnabled { get; init; }

    /// <summary>Gets the Agents SDK user-authorization handler name.</summary>
    public string AuthorizationHandler { get; init; } = "hr";

    /// <summary>Gets the durable authorization-state blob container URI.</summary>
    public string StorageContainerUri { get; init; } = string.Empty;

    /// <summary>Validates settings that are mandatory when Teams is enabled.</summary>
    public static bool IsValid(TeamsOptions options) =>
        !options.Enabled ||
        (Guid.TryParse(options.AppId, out _) &&
        Guid.TryParse(options.TenantId, out _) &&
        (!options.SsoEnabled || !string.IsNullOrWhiteSpace(options.AuthorizationHandler)));
}