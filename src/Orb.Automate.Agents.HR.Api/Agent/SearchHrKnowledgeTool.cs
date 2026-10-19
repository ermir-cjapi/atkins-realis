using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.Search;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Unified agent tool that fans out to SharePoint and ServiceNow in parallel, then delegates
/// score normalisation, de-duplication, and ranking to <see cref="KnowledgeMerger"/>. Returns a
/// single ranked passage list so the model reasons from one coherent context rather than two
/// separate feeds.
/// </summary>
public sealed class SearchHrKnowledgeTool(
    IOptions<SharePointSearchOptions> spOptions,
    IGroundingTelemetryContext groundingTelemetry,
    ILogger<SearchHrKnowledgeTool> logger,
    ISearchUserTokenProvider? tokenProvider = null,
    ISharePointSearchClient? spClient = null,
    ServiceNowKnowledgeRetriever? snRetriever = null,
    DevelopmentPayloadWriter? payloadWriter = null)
{
    private static readonly JsonSerializerOptions ModelJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, string, Exception?> LogFanOut =
        LoggerMessage.Define<string>(
            LogLevel.Information, new EventId(1, nameof(SearchHrKnowledgeAsync)),
            "search_hr_knowledge fan-out started for query: '{Query}'");

    private static readonly Action<ILogger, int, long, Exception?> LogSpSuccess =
        LoggerMessage.Define<int, long>(
            LogLevel.Information, new EventId(2, nameof(SearchHrKnowledgeAsync)),
            "SharePoint returned {ResultCount} passage(s) in {ElapsedMs} ms");

    private static readonly Action<ILogger, SharePointSearchFailureKind, Exception?> LogSpFailure =
        LoggerMessage.Define<SharePointSearchFailureKind>(
            LogLevel.Warning, new EventId(3, nameof(SearchHrKnowledgeAsync)),
            "SharePoint search within unified tool failed with {FailureKind}");

    private static readonly Action<ILogger, Exception?> LogSpTimeout =
        LoggerMessage.Define(
            LogLevel.Warning, new EventId(4, nameof(SearchHrKnowledgeAsync)),
            "SharePoint search within unified tool timed out");

    private static readonly Action<ILogger, string, Exception?> LogSpSkipped =
        LoggerMessage.Define<string>(
            LogLevel.Information, new EventId(5, nameof(SearchHrKnowledgeAsync)),
            "SharePoint skipped — {Reason}");

    private static readonly Action<ILogger, int, long, Exception?> LogSnSuccess =
        LoggerMessage.Define<int, long>(
            LogLevel.Information, new EventId(6, nameof(SearchHrKnowledgeAsync)),
            "ServiceNow returned {ResultCount} article(s) in {ElapsedMs} ms");

    private static readonly Action<ILogger, string, Exception?> LogSnSkipped =
        LoggerMessage.Define<string>(
            LogLevel.Information, new EventId(7, nameof(SearchHrKnowledgeAsync)),
            "ServiceNow skipped — {Reason}");

    private static readonly Action<ILogger, int, int, int, Exception?> LogMerged =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Information, new EventId(8, nameof(SearchHrKnowledgeAsync)),
            "Merged result: {TotalPassages} passage(s) returned to model ({SpPassages} SP + {SnPassages} SN after merge/rank)");

    private static readonly Action<ILogger, int, int, int, Exception?> LogRankingSummary =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Information, new EventId(9, nameof(SearchHrKnowledgeAsync)),
            "Ranking summary: {DedupedCount} deduped (SP+SN matched), {SpOnlyCount} SP-only, {SnOnlyCount} SN-only");

    private static readonly Action<ILogger, int, double, string, string, string, Exception?> LogPassage =
        LoggerMessage.Define<int, double, string, string, string>(
            LogLevel.Information, new EventId(10, nameof(SearchHrKnowledgeAsync)),
            "  #{Rank} score={Score:F3} source={Source,-12} {Title}{Flags}");

    /// <summary>
    /// Searches approved HR policy and guidance from SharePoint and ServiceNow simultaneously,
    /// then returns a single merged and ranked passage list.
    /// </summary>
    /// <param name="q">Two to four concise HR search keywords, including the country or region when relevant.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [Description(
        "Searches approved HR policy and guidance documents from both SharePoint and ServiceNow simultaneously. " +
        "Returns a single merged and ranked list of passages ready for citation. " +
        "Use two to four concise HR keywords, including the country or region when the question is location-specific. " +
        "Do NOT call this tool when a [PREVIOUS DRAFT] or [PREVIOUS OBJECTIVE] section is present alongside " +
        "a [REGENERATE INSTRUCTION] — the approved content is already carried in policySourceUrls.")]
    public async Task<SearchHrKnowledgeResult> SearchHrKnowledgeAsync(
        [Description("Two to four concise HR search keywords, including the country or region when relevant.")] string q,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return SearchHrKnowledgeResult.InvalidQuery();
        }

        // Fan out to both sources in parallel; each captures its own duration.
        LogFanOut(logger, q, null);
        var spTask = RunSharePointAsync(q, cancellationToken);
        var snTask = RunServiceNowAsync(q, cancellationToken);
        await Task.WhenAll(spTask, snTask);

        var (spResult, spDuration) = spTask.Result;
        var (snResult, snDuration) = snTask.Result;

        // Record individual-source telemetry so existing grounding metrics stay consistent.
        // RecordSharePoint also stores SP passages used later for citation validation.
        groundingTelemetry.RecordSharePoint(spResult, spDuration);
        groundingTelemetry.RecordServiceNow(snResult, snDuration);

        var merged = KnowledgeMerger.MergeAndRank(spResult, snResult, spOptions.Value);

        var spPassages = spResult.Results?.Count ?? 0;
        var snArticles = snResult.Results?.Count ?? 0;
        LogMerged(logger, merged.ReturnedCount, spPassages, snArticles, null);

        // Log ranking breakdown so the merge/rank logic can be verified without inspecting content.
        var dedupedCount  = merged.Passages.Count(p => p.Source == "both");
        var spOnlyCount   = merged.Passages.Count(p => p.Source == "sharepoint");
        var snOnlyCount   = merged.Passages.Count(p => p.Source == "servicenow");
        LogRankingSummary(logger, dedupedCount, spOnlyCount, snOnlyCount, null);
        foreach (var p in merged.Passages)
        {
            var title = p.Title.Length > 55 ? string.Concat(p.Title.AsSpan(0, 52), "...") : p.Title;
            var flags = string.Concat(
                p.KbNumber is not null ? $" [{p.KbNumber}]" : string.Empty,
                p.FreshnessFlag == "Stale" ? " [STALE]" : string.Empty,
                p.Conflict ? " [CONFLICT]" : string.Empty);
            LogPassage(logger, p.Rank, p.NormalisedScore, p.Source, title, flags, null);
        }

        payloadWriter?.Write("UNIFIED KNOWLEDGE TOOL RESPONSE", JsonSerializer.Serialize(merged, ModelJsonOptions));
        return merged;
    }

    // -------------------------------------------------------------------------
    // Source fan-out
    // -------------------------------------------------------------------------

    private async Task<(SearchHrPolicyDocumentsResult Result, TimeSpan Duration)> RunSharePointAsync(
        string q, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (spClient is null || tokenProvider is null)
        {
            LogSpSkipped(logger, "client or token provider not registered", null);
            return (SearchHrPolicyDocumentsResult.Unavailable(), Stopwatch.GetElapsedTime(started));
        }

        // A delegated token is only needed for permission trimming. Without enforcement no
        // on-behalf-of exchange is attempted, so missing delegated consent cannot block search.
        string? userToken = null;
        if (spOptions.Value.EnforceDocumentPermissions)
        {
            userToken = await tokenProvider.GetTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(userToken))
            {
                LogSpSkipped(logger, "user token unavailable", null);
                return (SearchHrPolicyDocumentsResult.IdentityUnavailable(), Stopwatch.GetElapsedTime(started));
            }
        }

        try
        {
            var response = await spClient.SearchAsync(q, userToken, cancellationToken);
            var result = SearchHrPolicyDocumentsTool.CreateModelResult(response, spOptions.Value);
            var elapsed = Stopwatch.GetElapsedTime(started);
            LogSpSuccess(logger, result.Results?.Count ?? 0, (long)elapsed.TotalMilliseconds, null);
            return (result, elapsed);
        }
        catch (SharePointSearchException ex)
        {
            LogSpFailure(logger, ex.Kind, ex);
            return (SearchHrPolicyDocumentsResult.Unavailable(), Stopwatch.GetElapsedTime(started));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogSpTimeout(logger, ex);
            return (SearchHrPolicyDocumentsResult.Unavailable(), Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<(GetScopedKnowledgeArticlesResult Result, TimeSpan Duration)> RunServiceNowAsync(
        string q, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (snRetriever is null)
        {
            LogSnSkipped(logger, "retriever not registered", null);
            return (GetScopedKnowledgeArticlesResult.Unavailable(), Stopwatch.GetElapsedTime(started));
        }

        var result = await snRetriever.SearchAsync(q, cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (result.Status == "success")
        {
            LogSnSuccess(logger, result.Results?.Count ?? 0, (long)elapsed.TotalMilliseconds, null);
        }
        return (result, elapsed);
    }
}

// =============================================================================
// Result contract
// =============================================================================

/// <summary>
/// The merged, ranked knowledge result returned to the model by <see cref="SearchHrKnowledgeTool"/>.
/// </summary>
/// <param name="Status">Controlled outcome: <c>success</c>, <c>no_results</c>, <c>unavailable</c>, or <c>invalid_query</c>.</param>
/// <param name="Message">Guidance for the model on how to interpret and cite the passages.</param>
/// <param name="ReturnedCount">Number of passages in <see cref="Passages"/>.</param>
/// <param name="Passages">Ordered passages, rank 1 first.</param>
public sealed record SearchHrKnowledgeResult(
    string Status,
    string Message,
    int ReturnedCount,
    IReadOnlyList<RankedKnowledgePassage> Passages)
{
    internal static SearchHrKnowledgeResult InvalidQuery() =>
        new("invalid_query",
            "Provide two to four concise HR search keywords, including location when relevant.",
            0, []);

    internal static SearchHrKnowledgeResult IdentityUnavailable() =>
        new("identity_unavailable",
            "The current user's verified identity is unavailable. " +
            "State that approved guidance could not be verified and recommend raising an HR Case.",
            0, []);
}

/// <summary>
/// A single merged and ranked knowledge passage returned to the model.
/// </summary>
/// <param name="Rank">1-based rank (1 = most relevant).</param>
/// <param name="NormalisedScore">Score in [0, 1] combining both source scales.</param>
/// <param name="Source">
/// <c>sharepoint</c> — content and URL come from SharePoint.<br/>
/// <c>servicenow</c> — content and KB number come from ServiceNow.<br/>
/// <c>both</c>       — same policy found in both; SharePoint content wins; KbNumber from ServiceNow.
/// </param>
/// <param name="Title">Document title for display.</param>
/// <param name="Content">The passage text to answer from.</param>
/// <param name="Url">
/// Canonical SharePoint HTTPS URL — include verbatim as a clickable link when citing this passage.
/// <see langword="null"/> for ServiceNow-only passages.
/// </param>
/// <param name="KbNumber">
/// ServiceNow KB number (e.g. <c>KB0012345</c>) — mention this when citing a ServiceNow passage.
/// <see langword="null"/> for SharePoint-only passages.
/// </param>
/// <param name="Category">ServiceNow category label, when available.</param>
/// <param name="LastModified">Last-modified timestamp from SharePoint, when available.</param>
/// <param name="FreshnessFlag"><c>Current</c> or <c>Stale</c>.</param>
/// <param name="Conflict">
/// <see langword="true"/> when the same policy was found in both sources but the content
/// contains different numeric values. Disclose both values and recommend the user confirm with HR.
/// </param>
/// <param name="ConflictNote">Human-readable conflict description when <paramref name="Conflict"/> is <see langword="true"/>.</param>
/// <param name="ConflictingContent">
/// The ServiceNow passage text when <paramref name="Conflict"/> is <see langword="true"/>, so both
/// values can be disclosed; otherwise <see langword="null"/>.
/// </param>
public sealed record RankedKnowledgePassage(
    int Rank,
    double NormalisedScore,
    string Source,
    string Title,
    string Content,
    string? Url,
    string? KbNumber,
    string? Category,
    DateTimeOffset? LastModified,
    string FreshnessFlag,
    bool Conflict,
    string? ConflictNote,
    string? ConflictingContent);
