namespace Orb.Automate.Agents.HR.Api.Options;

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
}
