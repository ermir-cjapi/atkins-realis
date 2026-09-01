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
    /// Gets the outbound HTTP timeout in seconds.
    /// </summary>
    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; init; } = 30;

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
