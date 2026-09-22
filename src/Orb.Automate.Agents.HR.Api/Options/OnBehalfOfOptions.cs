namespace Orb.Automate.Agents.HR.Api.Options;

/// <summary>Controls how delegated downstream tokens are acquired.</summary>
public enum SearchUserTokenMode
{
    /// <summary>Use the signed-in Azure CLI identity for local development.</summary>
    AzureCli,

    /// <summary>Exchange the validated inbound token on behalf of the user.</summary>
    OnBehalfOf
}

/// <summary>
/// Configuration for the on-behalf-of exchange that converts an inbound user token into a
/// token scoped to the Azure AI Search data plane.
/// </summary>
public sealed class OnBehalfOfOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "OnBehalfOf";

    /// <summary>
    /// Gets the Entra tenant identifier used for the exchange.
    /// </summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the client identifier of the API's app registration.
    /// </summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Gets the downstream token acquisition mode.</summary>
    public SearchUserTokenMode UserTokenMode { get; init; } = SearchUserTokenMode.AzureCli;

    /// <summary>Gets the development-only client secret used for local OBO testing.</summary>
    public string ClientSecret { get; init; } = string.Empty;
}
