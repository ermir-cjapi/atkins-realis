using System.Text.Json;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Retrieves and pre-processes ServiceNow AI-search results for consumption by
/// <see cref="SearchHrKnowledgeTool"/>. Handles identity resolution, the HTTP fan-out,
/// keyword scoring, and payload budgeting. Telemetry recording and payload logging are
/// the caller's responsibility.
/// </summary>
public sealed class ServiceNowKnowledgeRetriever(
    ICurrentUserUpnAccessor upnAccessor,
    IServiceNowAiSearchClient searchClient,
    IOptions<ServiceNowOptions> options,
    ILogger<ServiceNowKnowledgeRetriever> logger)
{
    private static readonly JsonSerializerOptions ModelJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, ServiceNowFailureKind, Exception?> LogSearchFailure =
        LoggerMessage.Define<ServiceNowFailureKind>(
            LogLevel.Warning,
            new EventId(1, nameof(SearchAsync)),
            "ServiceNow knowledge retrieval failed with {FailureKind}");

    /// <summary>
    /// Searches ServiceNow using the verified current-user UPN and returns a typed result.
    /// Telemetry recording and payload logging are performed by the caller.
    /// </summary>
    internal async Task<GetScopedKnowledgeArticlesResult> SearchAsync(
        string q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return GetScopedKnowledgeArticlesResult.InvalidQuery();
        }

        var upn = upnAccessor.GetCurrentUserUpn();
        if (string.IsNullOrWhiteSpace(upn))
        {
            return GetScopedKnowledgeArticlesResult.IdentityUnavailable();
        }

        try
        {
            var response = await searchClient.SearchAsync(upn, q, cancellationToken);
            return CreateModelResult(response, q, options.Value);
        }
        catch (ArgumentException)
        {
            return GetScopedKnowledgeArticlesResult.InvalidQuery();
        }
        catch (ServiceNowException ex)
        {
            LogSearchFailure(logger, ex.Kind, null);
            return GetScopedKnowledgeArticlesResult.Unavailable();
        }
        catch (HttpRequestException)
        {
            LogSearchFailure(logger, ServiceNowFailureKind.Unavailable, null);
            return GetScopedKnowledgeArticlesResult.Unavailable();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogSearchFailure(logger, ServiceNowFailureKind.Unavailable, null);
            return GetScopedKnowledgeArticlesResult.Unavailable();
        }
    }

    // -------------------------------------------------------------------------
    // Result processing — internal for unit-test access
    // -------------------------------------------------------------------------

    internal static GetScopedKnowledgeArticlesResult CreateModelResult(
        ServiceNowSearchResponse response,
        string query,
        ServiceNowOptions settings)
    {
        if (response.Results.Count == 0)
        {
            return new GetScopedKnowledgeArticlesResult(
                response.Source == ServiceNowSearchSource.StaleCache ? "stale" : "no_results",
                response.Source == ServiceNowSearchSource.StaleCache
                    ? "Only possibly stale ServiceNow data was available and it contained no relevant results. Prioritize current SharePoint evidence."
                    : "No relevant ServiceNow results were found. Use current SharePoint evidence alone.",
                response.Term,
                response.Count,
                0,
                []);
        }

        var queryTerms = GetQueryTerms(query);
        var results = response.Results
            .Select((result, index) => new { Result = result, Index = index, Score = Score(result, query, queryTerms) })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(settings.MaxLlmResults)
            .Select(item => Project(item.Result, settings.MaxLlmResultContentCharacters))
            .ToList();

        return ApplyPayloadBudget(
            new GetScopedKnowledgeArticlesResult(
                response.Source == ServiceNowSearchSource.StaleCache ? "stale" : "success",
                response.Source == ServiceNowSearchSource.StaleCache
                    ? "ServiceNow results may be stale. Prioritize current SharePoint evidence and disclose possible staleness if these results affect the answer."
                    : "ServiceNow search completed successfully. Use only results relevant to the user's topic and location.",
                response.Term,
                response.Count,
                results.Count,
                results),
            settings.MaxLlmPayloadCharacters);
    }

    internal static int Score(ServiceNowSearchResult result, string query, IReadOnlyList<string> queryTerms)
    {
        var score = string.Equals(result.Table, "kb_knowledge", StringComparison.OrdinalIgnoreCase) ? 10 : 0;
        if (result.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 80;
        }

        foreach (var term in queryTerms)
        {
            if (result.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            if (result.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 4;
            }
        }

        return score;
    }

    internal static IReadOnlyList<string> GetQueryTerms(string query) => query
        .Split(query.Where(static character => !char.IsLetterOrDigit(character)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries)
        .Where(term => term.Length > 1)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static CompactServiceNowSearchResult Project(ServiceNowSearchResult result, int maximumContentLength)
    {
        var columns = result.Columns
            .Where(column => !string.IsNullOrWhiteSpace(column.FieldName))
            .GroupBy(column => column.FieldName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(column => string.IsNullOrWhiteSpace(column.DisplayValue) ? column.Value : column.DisplayValue)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        var preferredContentField = string.Equals(result.Table, "kb_knowledge", StringComparison.OrdinalIgnoreCase)
            ? "text"
            : "description";
        var content = columns.GetValueOrDefault(preferredContentField);
        if (string.IsNullOrWhiteSpace(content))
        {
            content = columns.GetValueOrDefault("short_description");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            content = result.Text;
        }

        return new CompactServiceNowSearchResult(
            result.SysId,
            result.Table,
            result.Title,
            Truncate(content, maximumContentLength),
            EmptyToNull(columns.GetValueOrDefault("number")),
            EmptyToNull(columns.GetValueOrDefault("category")) ?? EmptyToNull(columns.GetValueOrDefault("kb_category")));
    }

    private static GetScopedKnowledgeArticlesResult ApplyPayloadBudget(
        GetScopedKnowledgeArticlesResult result,
        int maximumCharacters)
    {
        var results = result.Results.ToList();
        var budgeted = result with { Results = results };

        while (JsonSerializer.Serialize(budgeted, ModelJsonOptions).Length > maximumCharacters)
        {
            var longestIndex = results
                .Select((item, index) => new { Index = index, item.Content.Length })
                .Where(item => item.Length > 400)
                .OrderByDescending(item => item.Length)
                .Select(item => item.Index)
                .DefaultIfEmpty(-1)
                .First();

            if (longestIndex >= 0)
            {
                var item = results[longestIndex];
                results[longestIndex] = item with { Content = Truncate(item.Content, Math.Max(400, item.Content.Length - 500)) };
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

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : $"{value[..(maximumLength - 3)].TrimEnd()}...";

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

// =============================================================================
// Result contract
// =============================================================================

/// <summary>
/// A safe, serializable ServiceNow result returned to the model.
/// </summary>
public sealed record GetScopedKnowledgeArticlesResult(
    string Status,
    string Message,
    string? Term,
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<CompactServiceNowSearchResult> Results)
{
    internal static GetScopedKnowledgeArticlesResult InvalidQuery() =>
        new("invalid_query", "Provide two to four concise HR search keywords, including location when relevant.", null, 0, 0, []);

    internal static GetScopedKnowledgeArticlesResult IdentityUnavailable() =>
        new("identity_unavailable", "The current user's verified identity is unavailable, so ServiceNow guidance cannot be verified.", null, 0, 0, []);

    internal static GetScopedKnowledgeArticlesResult Unavailable() =>
        new("unavailable", "ServiceNow guidance is unavailable. Do not retry this tool; use externally connected SharePoint knowledge alone and do not substitute general model knowledge.", null, 0, 0, []);
}

/// <summary>
/// A compact, deduplicated ServiceNow result suitable for LLM context.
/// </summary>
public sealed record CompactServiceNowSearchResult(
    string SysId,
    string Table,
    string Title,
    string Content,
    string? Number,
    string? Category);
