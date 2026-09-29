using System.ComponentModel.DataAnnotations;

namespace Orb.Automate.Agents.HR.Api.Options;

/// <summary>
/// Configuration for the SharePoint knowledge search integration backed by Azure AI Search.
/// </summary>
public sealed class SharePointSearchOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "SharePointSearch";

    /// <summary>
    /// Gets whether SharePoint knowledge search is enabled.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the Azure AI Search service endpoint.
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of the index populated from the HR SharePoint site.
    /// </summary>
    public string IndexName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the semantic configuration used to rank results.
    /// </summary>
    public string SemanticConfigurationName { get; init; } = string.Empty;

    /// <summary>
    /// Gets whether the search service trims results to the documents the current user may read.
    /// When <see langword="false"/>, queries use elevated read, so every authenticated user can
    /// retrieve every indexed document. Disabling requires the elevated-read data-plane permission
    /// on the search service for the application identity.
    /// </summary>
    public bool EnforceDocumentPermissions { get; init; } = true;

    /// <summary>
    /// Gets the trusted SharePoint document-library URL used to convert Graph drive paths into
    /// canonical citation URLs. This is optional when the index already stores absolute URLs.
    /// </summary>
    public string CanonicalUrlBase { get; init; } = string.Empty;

    /// <summary>
    /// Gets the vector field queried through integrated vectorization.
    /// </summary>
    [Required]
    public string VectorFieldName { get; init; } = "text_vector";

    /// <summary>
    /// Gets the outbound request timeout in seconds.
    /// </summary>
    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Gets the maximum accepted search term length.
    /// </summary>
    [Range(1, 500)]
    public int MaxSearchTermCharacters { get; init; } = 100;

    /// <summary>
    /// Gets the number of results retrieved from the index before filtering.
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
    /// Gets the minimum reranker score a result must reach before it is offered to the agent.
    /// Requires calibration against representative HR questions before it is trusted. The index
    /// ranks on the boosted reranker score, which is not bounded by the usual 0-4 reranker range.
    /// </summary>
    [Range(0d, 100d)]
    public double MinimumRerankerScore { get; init; } = 1.8d;

    /// <summary>
    /// Gets the maximum number of complementary chunks retained from one source document.
    /// </summary>
    [Range(1, 5)]
    public int MaxChunksPerParent { get; init; } = 2;

    /// <summary>
    /// Gets whether the index contains the extended source metadata fields required by the
    /// production index schema.
    /// </summary>
    public bool IncludeExtendedMetadata { get; init; }

    /// <summary>
    /// Gets the optional trusted publication status required for a chunk to be used.
    /// </summary>
    public string RequiredPublicationStatus { get; init; } = string.Empty;
}
