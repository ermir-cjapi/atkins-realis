namespace Orb.Automate.Agents.HR.Api.Search;

/// <summary>
/// Controlled categories of SharePoint knowledge search failure.
/// </summary>
public enum SharePointSearchFailureKind
{
    /// <summary>
    /// The index could not be reached, or the service reported a transient error.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The caller is not permitted to query the index.
    /// </summary>
    AccessDenied,

    /// <summary>
    /// The service returned a response that could not be interpreted.
    /// </summary>
    InvalidResponse,
}

/// <summary>
/// Raised when a SharePoint knowledge search cannot be completed.
/// </summary>
public sealed class SharePointSearchException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SharePointSearchException"/> class.
    /// </summary>
    public SharePointSearchException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharePointSearchException"/> class.
    /// </summary>
    /// <param name="message">The failure message.</param>
    public SharePointSearchException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharePointSearchException"/> class.
    /// </summary>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SharePointSearchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharePointSearchException"/> class.
    /// </summary>
    /// <param name="kind">The failure category.</param>
    /// <param name="message">The failure message.</param>
    public SharePointSearchException(SharePointSearchFailureKind kind, string message)
        : base(message) => Kind = kind;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharePointSearchException"/> class.
    /// </summary>
    /// <param name="kind">The failure category.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SharePointSearchException(SharePointSearchFailureKind kind, string message, Exception innerException)
        : base(message, innerException) => Kind = kind;

    /// <summary>
    /// Gets the failure category.
    /// </summary>
    public SharePointSearchFailureKind Kind { get; } = SharePointSearchFailureKind.Unavailable;
}

/// <summary>
/// A single ranked chunk returned by the SharePoint knowledge index.
/// </summary>
/// <param name="ChunkId">The chunk key.</param>
/// <param name="ParentId">The identifier of the source document the chunk belongs to.</param>
/// <param name="Title">The source document title as indexed.</param>
/// <param name="Content">The chunk text.</param>
/// <param name="Url">The absolute SharePoint item URL used for citations.</param>
/// <param name="Score">The reranker score used for ordering and thresholding.</param>
/// <param name="SourceVersion">The source content version, when indexed.</param>
/// <param name="LastModified">The source modification time, when indexed.</param>
/// <param name="PublicationStatus">The trusted source publication status, when indexed.</param>
/// <param name="Country">The country scope, when indexed.</param>
/// <param name="Jurisdiction">The jurisdiction scope, when indexed.</param>
/// <param name="Audience">The intended audience, when indexed.</param>
public sealed record SharePointSearchResult(
    string ChunkId,
    string ParentId,
    string Title,
    string Content,
    string Url,
    double Score,
    string SourceVersion = "",
    DateTimeOffset? LastModified = null,
    string PublicationStatus = "published",
    string Country = "",
    string Jurisdiction = "",
    string Audience = "");

/// <summary>
/// The outcome of a SharePoint knowledge search.
/// </summary>
/// <param name="Term">The search term submitted to the index.</param>
/// <param name="Results">The retrieved results, before agent-facing filtering.</param>
public sealed record SharePointSearchResponse(
    string Term,
    IReadOnlyList<SharePointSearchResult> Results);
