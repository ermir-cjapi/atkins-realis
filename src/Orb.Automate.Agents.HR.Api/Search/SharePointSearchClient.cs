using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Search;

/// <summary>
/// Queries the SharePoint knowledge index using hybrid retrieval and the semantic ranker.
/// Document-level trimming is enforced by the search service from the forwarded user token.
/// </summary>
/// <param name="credential">The application credential used to authorize access to the index.</param>
/// <param name="options">The SharePoint search configuration.</param>
public sealed class SharePointSearchClient(
    TokenCredential credential,
    IOptions<SharePointSearchOptions> options) : ISharePointSearchClient
{
    /// <summary>
    /// The chunk key field.
    /// </summary>
    internal const string ChunkIdField = "chunk_id";

    /// <summary>
    /// The field identifying the source document a chunk belongs to.
    /// </summary>
    internal const string ParentIdField = "parent_id";

    /// <summary>
    /// The chunk content field.
    /// </summary>
    internal const string ChunkField = "chunk";

    /// <summary>
    /// The document title field.
    /// </summary>
    internal const string TitleField = "title";

    /// <summary>
    /// The SharePoint item URL field used for citations.
    /// </summary>
    internal const string UrlField = "metadata_spo_item_path";

    private const string SourceVersionField = "source_version";
    private const string LastModifiedField = "last_modified";
    private const string PublicationStatusField = "publication_status";
    private const string CountryField = "country";
    private const string JurisdictionField = "jurisdiction";
    private const string AudienceField = "audience";

    private readonly SharePointSearchOptions settings = options.Value;
    private readonly SearchClient searchClient = CreateSearchClient(credential, options.Value);

    /// <inheritdoc />
    public async Task<SharePointSearchResponse> SearchAsync(
        string term,
        string userToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        ArgumentException.ThrowIfNullOrWhiteSpace(userToken);

        var searchTerm = term.Length > settings.MaxSearchTermCharacters
            ? term[..settings.MaxSearchTermCharacters]
            : term;

        var searchOptions = new SearchOptions
        {
            Size = settings.MaxResults,
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = settings.SemanticConfigurationName,
            },
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizableTextQuery(searchTerm)
                    {
                        KNearestNeighborsCount = settings.MaxResults,
                        Fields = { settings.VectorFieldName },
                    },
                },
            },
            Select = { ChunkIdField, ParentIdField, TitleField, ChunkField, UrlField },
        };

        if (settings.IncludeExtendedMetadata)
        {
            searchOptions.Select.Add(SourceVersionField);
            searchOptions.Select.Add(LastModifiedField);
            searchOptions.Select.Add(PublicationStatusField);
            searchOptions.Select.Add(CountryField);
            searchOptions.Select.Add(JurisdictionField);
            searchOptions.Select.Add(AudienceField);
        }

        Response<SearchResults<SearchDocument>> response;
        try
        {
            response = await searchClient.SearchAsync<SearchDocument>(
                searchTerm,
                userToken,
                enableElevatedRead: settings.EnforceDocumentPermissions ? null : true,
                options: searchOptions,
                cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw new SharePointSearchException(
                MapFailure(exception.Status),
                "The SharePoint knowledge index could not be queried.",
                exception);
        }

        var results = new List<SharePointSearchResult>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            var document = result.Document;
            results.Add(new SharePointSearchResult(
                ReadString(document, ChunkIdField),
                ReadString(document, ParentIdField),
                ReadString(document, TitleField),
                ReadString(document, ChunkField),
                CreateCanonicalUrl(ReadString(document, UrlField), settings.CanonicalUrlBase),
                ReadScore(result),
                ReadString(document, SourceVersionField),
                ReadDateTimeOffset(document, LastModifiedField),
                ReadString(document, PublicationStatusField),
                ReadString(document, CountryField),
                ReadString(document, JurisdictionField),
                ReadString(document, AudienceField)));
        }

        return new SharePointSearchResponse(searchTerm, results);
    }

    private static SearchClient CreateSearchClient(TokenCredential credential, SharePointSearchOptions settings)
    {
        // Permission filtering is only honoured by preview service versions; a stable version would
        // silently ignore the forwarded user token and return untrimmed results.
        var clientOptions = new SearchClientOptions(SearchClientOptions.ServiceVersion.V2026_08_01_Preview);
        clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
        return new SearchClient(new Uri(settings.Endpoint), settings.IndexName, credential, clientOptions);
    }

    private static string ReadString(SearchDocument document, string field) =>
        document.TryGetValue(field, out var value) && value is string text ? text : string.Empty;

    private static DateTimeOffset? ReadDateTimeOffset(SearchDocument document, string field) =>
        document.TryGetValue(field, out var value) ? value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(dateTime),
            string text when DateTimeOffset.TryParse(text, out var parsed) => parsed,
            _ => null,
        } : null;

    /// <summary>
    /// Converts an indexed Microsoft Graph drive path to a canonical SharePoint library URL.
    /// Absolute HTTPS URLs are returned unchanged.
    /// </summary>
    /// <param name="indexedPath">The indexed path or absolute URL.</param>
    /// <param name="canonicalUrlBase">The trusted SharePoint document-library base URL.</param>
    /// <returns>An absolute canonical URL, or the original value when it cannot be safely mapped.</returns>
    internal static string CreateCanonicalUrl(string indexedPath, string canonicalUrlBase)
    {
        if (Uri.TryCreate(indexedPath, UriKind.Absolute, out var absolute) && absolute.Scheme == Uri.UriSchemeHttps)
        {
            return absolute.AbsoluteUri;
        }

        const string rootMarker = "/root:/";
        var markerIndex = indexedPath.IndexOf(rootMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0 ||
            !Uri.TryCreate(canonicalUrlBase.TrimEnd('/') + "/", UriKind.Absolute, out var libraryUrl) ||
            libraryUrl.Scheme != Uri.UriSchemeHttps)
        {
            return indexedPath;
        }

        var segments = indexedPath[(markerIndex + rootMarker.Length)..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return indexedPath;
        }

        var escapedPath = string.Join('/', segments.Select(segment => Uri.EscapeDataString(Uri.UnescapeDataString(segment))));
        return new Uri(libraryUrl, escapedPath).AbsoluteUri;
    }

    // The index sets rankingOrder to BoostedRerankerScore, so the boosted score is authoritative
    // when present.
    private static double ReadScore(SearchResult<SearchDocument> result) =>
        result.SemanticSearch?.RerankerBoostedScore
        ?? result.SemanticSearch?.RerankerScore
        ?? result.Score
        ?? 0d;

    private static SharePointSearchFailureKind MapFailure(int status) => status switch
    {
        401 or 403 => SharePointSearchFailureKind.AccessDenied,
        400 or 404 => SharePointSearchFailureKind.InvalidResponse,
        _ => SharePointSearchFailureKind.Unavailable,
    };
}
