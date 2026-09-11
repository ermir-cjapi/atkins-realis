namespace Orb.Automate.Agents.HR.Api.ServiceNow;

/// <summary>
/// A normalized ServiceNow AI Search response suitable for use by the agent.
/// </summary>
/// <param name="Term">The term reported by ServiceNow.</param>
/// <param name="Count">The total result count reported by ServiceNow.</param>
/// <param name="Results">The bounded result collection.</param>
/// <param name="Source">The live or cache provenance.</param>
/// <param name="RetrievedAt">When the live result was retrieved.</param>
public sealed record ServiceNowSearchResponse(
    string Term,
    int Count,
    IReadOnlyList<ServiceNowSearchResult> Results,
    ServiceNowSearchSource Source = ServiceNowSearchSource.Live,
    DateTimeOffset? RetrievedAt = null);

/// <summary>
/// Identifies where a successful search result originated without exposing search inputs.
/// </summary>
public enum ServiceNowSearchSource
{
    /// <summary>The result came from ServiceNow during this call.</summary>
    Live,

    /// <summary>The result came from a fresh per-instance cache entry.</summary>
    FreshCache,

    /// <summary>The result is stale cache data returned after live retrieval failed.</summary>
    StaleCache
}

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

    /// <summary>The aggregate ServiceNow turn budget expired.</summary>
    BudgetExpired,

    /// <summary>The shared search circuit is open.</summary>
    CircuitOpen,

    /// <summary>The local rate limiter rejected the request.</summary>
    RateLimited,

    /// <summary>The remote service applied a rate limit.</summary>
    RemoteThrottled,

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
    public ServiceNowException(ServiceNowFailureKind kind, string message, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the stable failure category.
    /// </summary>
    public ServiceNowFailureKind Kind { get; }

    /// <summary>
    /// Gets the bounded server-supplied retry delay, when a remote throttle supplied one.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
