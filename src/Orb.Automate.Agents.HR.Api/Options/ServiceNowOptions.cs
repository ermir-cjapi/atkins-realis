using System.ComponentModel.DataAnnotations;

namespace Orb.Automate.Agents.HR.Api.Options;

/// <summary>
/// Configuration for the ServiceNow AI Search integration.
/// </summary>
public sealed class ServiceNowOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "ServiceNow";

    /// <summary>
    /// Gets whether ServiceNow search is enabled.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the ServiceNow instance base URL.
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Gets the ServiceNow OAuth token endpoint.
    /// </summary>
    public string TokenEndpoint { get; init; } = string.Empty;

    /// <summary>
    /// Gets the AI Search API path.
    /// </summary>
    [Required]
    public string SearchPath { get; init; } = "/api/atki2/ai_search";

    /// <summary>
    /// Gets the ServiceNow OAuth client identifier.
    /// </summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the ServiceNow OAuth client secret.
    /// </summary>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>
    /// Gets a local test UPN that is permitted only in Development.
    /// </summary>
    [EmailAddress]
    public string? DevelopmentUserUpn { get; init; }

    /// <summary>
    /// Gets the aggregate ServiceNow work budget for one agent turn.
    /// </summary>
    [Range(1, 30)]
    public int TurnBudgetSeconds { get; init; } = 3;

    /// <summary>
    /// Gets the timeout applied to each AI Search HTTP attempt.
    /// </summary>
    [Range(100, 30_000)]
    public int SearchAttemptTimeoutMilliseconds { get; init; } = 2_000;

    /// <summary>
    /// Gets the timeout applied to each OAuth HTTP attempt.
    /// </summary>
    [Range(100, 30_000)]
    public int OAuthAttemptTimeoutMilliseconds { get; init; } = 1_000;

    /// <summary>
    /// Gets the number of transient retries after the initial search attempt.
    /// </summary>
    [Range(0, 1)]
    public int SearchRetryCount { get; init; } = 1;

    /// <summary>
    /// Gets the number of transient retries after the initial OAuth attempt.
    /// </summary>
    [Range(0, 1)]
    public int OAuthRetryCount { get; init; } = 1;

    /// <summary>
    /// Gets the base delay for short jittered retries.
    /// </summary>
    [Range(1, 1_000)]
    public int RetryBaseDelayMilliseconds { get; init; } = 100;

    /// <summary>
    /// Gets the circuit-breaker sampling window.
    /// </summary>
    [Range(1, 300)]
    public int CircuitSamplingSeconds { get; init; } = 30;

    /// <summary>
    /// Gets the circuit-breaker failure ratio.
    /// </summary>
    [Range(0.01, 1.0)]
    public double CircuitFailureRatio { get; init; } = 0.5;

    /// <summary>
    /// Gets the minimum attempt throughput before the circuit can open.
    /// </summary>
    [Range(2, 1_000)]
    public int CircuitMinimumThroughput { get; init; } = 10;

    /// <summary>
    /// Gets how long an open circuit rejects calls.
    /// </summary>
    [Range(1, 300)]
    public int CircuitBreakSeconds { get; init; } = 30;

    /// <summary>
    /// Gets the provisional sustained ServiceNow request rate per second.
    /// </summary>
    [Range(1, 1_000)]
    public int RateLimitRequestsPerSecond { get; init; } = 5;

    /// <summary>
    /// Gets the token-bucket burst capacity.
    /// </summary>
    [Range(1, 1_000)]
    public int RateLimitBurst { get; init; } = 5;

    /// <summary>
    /// Gets the maximum number of requests waiting for a rate permit.
    /// </summary>
    [Range(0, 1_000)]
    public int RateLimitQueueLimit { get; init; } = 5;

    /// <summary>
    /// Gets the fresh result-cache lifetime.
    /// </summary>
    [Range(1, 1_440)]
    public int CacheFreshMinutes { get; init; } = 15;

    /// <summary>
    /// Gets the maximum age at which a result may be used as stale fallback.
    /// </summary>
    [Range(1, 10_080)]
    public int CacheMaximumStaleMinutes { get; init; } = 60;

    /// <summary>
    /// Gets the size limit for the per-instance result cache, in entries.
    /// </summary>
    [Range(1, 100_000)]
    public int CacheSizeLimit { get; init; } = 1_000;

    /// <summary>
    /// Gets an operator-controlled salt used for bulk cache invalidation.
    /// </summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string CacheVersion { get; init; } = "v1";

    /// <summary>
    /// Gets the maximum accepted search term length.
    /// </summary>
    [Range(1, 500)]
    public int MaxSearchTermCharacters { get; init; } = 100;

    /// <summary>
    /// Gets the maximum number of results returned to the agent.
    /// </summary>
    [Range(1, 50)]
    public int MaxResults { get; init; } = 12;

    /// <summary>
    /// Gets the maximum number of ranked results included in the LLM tool response.
    /// </summary>
    [Range(1, 10)]
    public int MaxLlmResults { get; init; } = 5;

    /// <summary>
    /// Gets the maximum content length for one result in the LLM tool response.
    /// </summary>
    [Range(200, 10_000)]
    public int MaxLlmResultContentCharacters { get; init; } = 2_500;

    /// <summary>
    /// Gets the maximum serialized character count for the complete LLM tool response.
    /// </summary>
    [Range(1_000, 50_000)]
    public int MaxLlmPayloadCharacters { get; init; } = 10_000;

    /// <summary>
    /// Gets the maximum title length returned to the agent.
    /// </summary>
    [Range(1, 2_000)]
    public int MaxTitleCharacters { get; init; } = 500;

    /// <summary>
    /// Gets the maximum result text length returned to the agent.
    /// </summary>
    [Range(1, 20_000)]
    public int MaxTextCharacters { get; init; } = 8_000;

    /// <summary>
    /// Gets the maximum number of metadata columns returned for each result.
    /// </summary>
    [Range(1, 50)]
    public int MaxColumns { get; init; } = 20;

    /// <summary>
    /// Gets the maximum metadata value length returned to the agent.
    /// </summary>
    [Range(1, 20_000)]
    public int MaxColumnValueCharacters { get; init; } = 8_000;
}
