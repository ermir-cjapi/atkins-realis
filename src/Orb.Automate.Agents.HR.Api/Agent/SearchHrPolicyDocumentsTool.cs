using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.Search;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Agent function that retrieves approved HR policy and guidance content from the SharePoint
/// knowledge index, trimmed to the current user's permissions.
/// </summary>
/// <param name="tokenProvider">Supplies the current user's search-scoped token.</param>
/// <param name="searchClient">Queries the SharePoint knowledge index.</param>
/// <param name="options">The SharePoint search configuration.</param>
/// <param name="logger">The logger used for tool invocation events.</param>
public sealed class SearchHrPolicyDocumentsTool(
    ISearchUserTokenProvider tokenProvider,
    ISharePointSearchClient searchClient,
    IOptions<SharePointSearchOptions> options,
    ILogger<SearchHrPolicyDocumentsTool> logger)
{
    private const string Ellipsis = "...";
    private const int MinimumContentCharacters = 400;
    private const int ContentReductionStep = 500;

    private static readonly JsonSerializerOptions ModelJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] DocumentExtensions =
    [
        ".aspx", ".doc", ".docx", ".htm", ".html", ".md", ".pdf", ".ppt", ".pptx", ".txt", ".xls", ".xlsx",
    ];

    private static readonly Action<ILogger, SharePointSearchFailureKind, Exception?> LogSearchFailure =
        LoggerMessage.Define<SharePointSearchFailureKind>(
            LogLevel.Warning,
            new EventId(1, nameof(SearchHrPolicyDocumentsAsync)),
            "SharePoint knowledge search failed with category {FailureKind}");

    private static readonly Action<ILogger, string, int, int, Exception?> LogRetrieval =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Information,
            new EventId(2, nameof(SearchHrPolicyDocumentsAsync)),
            "SharePoint knowledge search returned status {Status} with {RetrievedCount} retrieved and {ReturnedCount} returned results");

    /// <summary>
    /// Searches approved HR SharePoint content using generated HR keywords.
    /// </summary>
    /// <param name="q">One to three concise HR search keywords.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A bounded, citable result set for the agent.</returns>
    [Description("Searches approved HR policy and guidance documents published on the HR SharePoint site. Use one to three concise HR keywords, including the country or region when the question is location-specific.")]
    public async Task<SearchHrPolicyDocumentsResult> SearchHrPolicyDocumentsAsync(
        [Description("One to three concise HR search keywords, including the country or region when relevant.")] string q,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return LogAndReturn(SearchHrPolicyDocumentsResult.InvalidQuery());
        }

        var userToken = await tokenProvider.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(userToken))
        {
            return LogAndReturn(SearchHrPolicyDocumentsResult.IdentityUnavailable());
        }

        try
        {
            var response = await searchClient.SearchAsync(q, userToken, cancellationToken);
            return LogAndReturn(CreateModelResult(response, options.Value));
        }
        catch (ArgumentException)
        {
            return LogAndReturn(SearchHrPolicyDocumentsResult.InvalidQuery());
        }
        catch (SharePointSearchException exception)
        {
            LogSearchFailure(logger, exception.Kind, exception);
            return LogAndReturn(SearchHrPolicyDocumentsResult.Unavailable());
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogSearchFailure(logger, SharePointSearchFailureKind.Unavailable, exception);
            return LogAndReturn(SearchHrPolicyDocumentsResult.Unavailable());
        }
    }

    /// <summary>
    /// Filters, deduplicates and bounds retrieved results for the model.
    /// </summary>
    /// <param name="response">The raw search response.</param>
    /// <param name="settings">The SharePoint search configuration.</param>
    /// <returns>The agent-facing result.</returns>
    internal static SearchHrPolicyDocumentsResult CreateModelResult(
        SharePointSearchResponse response,
        SharePointSearchOptions settings)
    {
        var retrievedCount = response.Results.Count;
        var results = response.Results
            .Where(result => result.Score >= settings.MinimumRerankerScore)
            .Where(IsValidCanonicalUrl)
            .Where(result => string.IsNullOrWhiteSpace(settings.RequiredPublicationStatus) ||
                string.Equals(result.PublicationStatus, settings.RequiredPublicationStatus, StringComparison.OrdinalIgnoreCase))
            .GroupBy(GetDocumentKey, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group
                .OrderByDescending(item => item.Score)
                .Take(settings.MaxChunksPerParent))
            .OrderByDescending(result => result.Score)
            .Take(settings.MaxLlmResults)
            .Select(result => Project(result, settings))
            .ToList();

        return results.Count == 0
            ? SearchHrPolicyDocumentsResult.NoResults(response.Term, retrievedCount)
            : ApplyPayloadBudget(
                new SearchHrPolicyDocumentsResult(
                    "success",
                    "SharePoint knowledge search completed successfully. Use only results relevant to the user's topic and location, and cite the supplied url values without modifying them.",
                    response.Term,
                    retrievedCount,
                    results.Count,
                    results),
                settings.MaxLlmPayloadCharacters);
    }

    /// <summary>
    /// Removes a document extension and separator characters from an indexed file name.
    /// </summary>
    /// <param name="title">The indexed title.</param>
    /// <param name="maximumLength">The maximum returned length.</param>
    /// <returns>A title suitable for use as a link label.</returns>
    internal static string NormaliseTitle(string title, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var trimmed = title.Trim();
        foreach (var extension in DocumentExtensions)
        {
            if (trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^extension.Length];
                break;
            }
        }

        var separated = trimmed.Replace('_', ' ');
        var collapsed = string.Join(' ', separated.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Truncate(collapsed.Length == 0 ? trimmed : collapsed, maximumLength);
    }

    private static string GetDocumentKey(SharePointSearchResult result) =>
        string.IsNullOrWhiteSpace(result.ParentId) ? result.ChunkId : result.ParentId;

    private static HrPolicyDocumentResult Project(SharePointSearchResult result, SharePointSearchOptions settings) =>
        new(
            result.ChunkId,
            result.ParentId,
            NormaliseTitle(result.Title, settings.MaxTitleCharacters),
            Truncate(result.Content, settings.MaxLlmResultContentCharacters),
            result.Url,
            Math.Round(result.Score, 3),
            result.SourceVersion,
            result.LastModified,
            result.Country,
            result.Jurisdiction,
            result.Audience);

    private static bool IsValidCanonicalUrl(SharePointSearchResult result) =>
        Uri.TryCreate(result.Url, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps;

    private static SearchHrPolicyDocumentsResult ApplyPayloadBudget(
        SearchHrPolicyDocumentsResult result,
        int maximumCharacters)
    {
        var results = result.Results.ToList();
        var budgeted = result with { Results = results };

        while (JsonSerializer.Serialize(budgeted, ModelJsonOptions).Length > maximumCharacters)
        {
            var longestIndex = FindLongestReducibleContent(results);
            if (longestIndex >= 0)
            {
                var item = results[longestIndex];
                var reduced = item.Content.Length - ContentReductionStep;
                results[longestIndex] = item with
                {
                    Content = Truncate(item.Content, Math.Max(MinimumContentCharacters, reduced)),
                };
            }
            else if (results.Count > 1)
            {
                results.RemoveAt(results.Count - 1);
            }
            else
            {
                break;
            }

            budgeted = budgeted with { ReturnedCount = results.Count, Results = results };
        }

        return budgeted;
    }

    private static int FindLongestReducibleContent(List<HrPolicyDocumentResult> results)
    {
        var longestIndex = -1;
        var longestLength = MinimumContentCharacters;

        for (var index = 0; index < results.Count; index++)
        {
            if (results[index].Content.Length > longestLength)
            {
                longestIndex = index;
                longestLength = results[index].Content.Length;
            }
        }

        return longestIndex;
    }

    private static string Truncate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var cutoff = Math.Max(0, maximumLength - Ellipsis.Length);
        return string.Concat(value[..cutoff].TrimEnd(), Ellipsis);
    }

    private SearchHrPolicyDocumentsResult LogAndReturn(SearchHrPolicyDocumentsResult result)
    {
        LogRetrieval(logger, result.Status, result.RetrievedCount, result.ReturnedCount, null);
        return result;
    }
}

/// <summary>
/// A safe, serializable result returned to the model by the SharePoint knowledge tool.
/// </summary>
/// <param name="Status">The controlled outcome category.</param>
/// <param name="Message">Guidance for the model on how to treat the payload.</param>
/// <param name="Term">The search term submitted to the index.</param>
/// <param name="RetrievedCount">The number of results retrieved before filtering.</param>
/// <param name="ReturnedCount">The number of results supplied to the model.</param>
/// <param name="Results">The bounded results.</param>
public sealed record SearchHrPolicyDocumentsResult(
    string Status,
    string Message,
    string? Term,
    int RetrievedCount,
    int ReturnedCount,
    IReadOnlyList<HrPolicyDocumentResult> Results)
{
    internal static SearchHrPolicyDocumentsResult InvalidQuery() =>
        new(
            "invalid_query",
            "Provide one to three concise HR search keywords, including location when relevant.",
            null,
            0,
            0,
            []);

    internal static SearchHrPolicyDocumentsResult IdentityUnavailable() =>
        new(
            "identity_unavailable",
            "The current user's verified identity is unavailable, so SharePoint guidance cannot be retrieved or verified.",
            null,
            0,
            0,
            []);

    internal static SearchHrPolicyDocumentsResult NoResults(string term, int retrievedCount) =>
        new(
            "no_results",
            "No approved SharePoint guidance was found for these keywords.",
            term,
            retrievedCount,
            0,
            []);

    internal static SearchHrPolicyDocumentsResult Unavailable() =>
        new(
            "unavailable",
            "SharePoint guidance could not be retrieved or verified.",
            null,
            0,
            0,
            []);
}

/// <summary>
/// A bounded, citable SharePoint chunk suitable for LLM context.
/// </summary>
/// <param name="ChunkId">The stable retrieval and chunk identifier.</param>
/// <param name="ParentId">The source-document identifier.</param>
/// <param name="Title">The normalised document title, usable as a link label.</param>
/// <param name="Content">The chunk text.</param>
/// <param name="Url">The absolute SharePoint URL to cite without modification.</param>
/// <param name="Score">The reranker score.</param>
/// <param name="SourceVersion">The source content version, when available.</param>
/// <param name="LastModified">The source modification time, when available.</param>
/// <param name="Country">The country scope, when available.</param>
/// <param name="Jurisdiction">The jurisdiction scope, when available.</param>
/// <param name="Audience">The intended audience, when available.</param>
public sealed record HrPolicyDocumentResult(
    string ChunkId,
    string ParentId,
    string Title,
    string Content,
    string Url,
    double Score,
    string SourceVersion,
    DateTimeOffset? LastModified,
    string Country,
    string Jurisdiction,
    string Audience);
