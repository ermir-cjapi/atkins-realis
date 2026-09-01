namespace Orb.Automate.Agents.HR.Api.ServiceNow;

/// <summary>
/// A normalized ServiceNow AI Search response suitable for use by the agent.
/// </summary>
/// <param name="Term">The term reported by ServiceNow.</param>
/// <param name="Count">The total result count reported by ServiceNow.</param>
/// <param name="Results">The bounded result collection.</param>
public sealed record ServiceNowSearchResponse(
    string Term,
    int Count,
    IReadOnlyList<ServiceNowSearchResult> Results);

/// <summary>
/// A normalized ServiceNow search result.
/// </summary>
public sealed record ServiceNowSearchResult(
    string SysId,
    string Table,
    string Title,
    string Text,
    IReadOnlyList<ServiceNowSearchColumn> Columns);

/// <summary>
/// A normalized metadata column from a ServiceNow result.
/// </summary>
public sealed record ServiceNowSearchColumn(
    string FieldName,
    string Label,
    string Value,
    string DisplayValue);

/// <summary>
/// The stable categories exposed for ServiceNow integration failures.
/// </summary>
public enum ServiceNowFailureKind
{
    /// <summary>The integration is disabled or misconfigured.</summary>
    Configuration,

    /// <summary>OAuth authentication failed.</summary>
    Authentication,

    /// <summary>The configured identity lacks access.</summary>
    AccessDenied,

    /// <summary>The remote service applied a rate limit.</summary>
    Throttled,

    /// <summary>The remote service is temporarily unavailable.</summary>
    Unavailable,

    /// <summary>The remote response did not match the expected contract.</summary>
    InvalidResponse
}

/// <summary>
/// Represents a controlled ServiceNow integration failure.
/// </summary>
public sealed class ServiceNowException : Exception
{
    /// <summary>
    /// Creates a controlled ServiceNow integration exception.
    /// </summary>
    public ServiceNowException(ServiceNowFailureKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>
    /// Gets the stable failure category.
    /// </summary>
    public ServiceNowFailureKind Kind { get; }
}
