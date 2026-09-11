using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Orb.Automate.Agents.HR.Api.ServiceNow;

/// <summary>
/// Searches the ServiceNow AI Search endpoint.
/// </summary>
public interface IServiceNowAiSearchClient
{
    /// <summary>
    /// Searches ServiceNow using a verified UPN and generated keywords.
    /// </summary>
    Task<ServiceNowSearchResponse> SearchAsync(string upn, string query, CancellationToken cancellationToken);
}

/// <summary>
/// HTTP implementation of the ServiceNow AI Search client with per-instance fresh/stale caching.
/// </summary>
public sealed class ServiceNowAiSearchClient(
    IHttpClientFactory httpClientFactory,
    IServiceNowOAuthTokenProvider tokenProvider,
    IServiceNowResultCache resultCache,
    IServiceNowTurnContextAccessor turnAccessor,
    TimeProvider timeProvider,
    IOptions<ServiceNowOptions> options,
    DevelopmentPayloadWriter payloadWriter) : IServiceNowAiSearchClient
{
    internal const string HttpClientName = "ServiceNowAiSearch";

    /// <inheritdoc />
    public async Task<ServiceNowSearchResponse> SearchAsync(
        string upn,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upn);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var settings = options.Value;
        var normalizedQuery = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var normalizedUpn = upn.Trim().ToLowerInvariant();
        if (normalizedQuery.Length > settings.MaxSearchTermCharacters)
        {
            throw new ArgumentException(
                $"Search terms cannot exceed {settings.MaxSearchTermCharacters} characters.",
                nameof(query));
        }

        var key = resultCache.CreateKey(normalizedUpn, normalizedQuery.ToUpperInvariant());
        var fresh = GetFresh(key, settings);
        if (fresh is not null)
        {
            ServiceNowDiagnostics.RecordCache("fresh");
            return fresh;
        }

        ServiceNowDiagnostics.RecordCache("miss");
        var turn = turnAccessor.Current;
        if (turn is null)
        {
            return await SearchWithCacheAsync(key, normalizedUpn, normalizedQuery, cancellationToken, null);
        }

        if (turn.IsExpired)
        {
            return GetStaleOrThrow(key, settings, new ServiceNowException(
                ServiceNowFailureKind.BudgetExpired,
                "The ServiceNow turn budget expired."));
        }

        ServiceNowDiagnostics.RecordRemainingBudget(turn.Remaining);
        return await turn.GetOrAdd(
            key,
            () => SearchWithCacheAsync(key, normalizedUpn, normalizedQuery, cancellationToken, turn));
    }

    private async Task<ServiceNowSearchResponse> SearchWithCacheAsync(
        string key,
        string normalizedUpn,
        string normalizedQuery,
        CancellationToken callerToken,
        ServiceNowTurnContext? turn)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = ServiceNowDiagnostics.ActivitySource.StartActivity("servicenow.search");
        using var linkedSource = turn is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(callerToken, turn.BudgetToken);
        var effectiveToken = linkedSource?.Token ?? callerToken;

        try
        {
            using var gate = await resultCache.AcquireAsync(key, effectiveToken);
            var settings = options.Value;
            var fresh = GetFresh(key, settings);
            if (fresh is not null)
            {
                ServiceNowDiagnostics.RecordCache("fresh_after_wait");
                return fresh;
            }

            try
            {
                var response = await SearchLiveAsync(normalizedUpn, normalizedQuery, settings, effectiveToken);
                resultCache.Set(key, response);
                return response;
            }
            catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested && turn?.IsExpired == true)
            {
                var failure = new ServiceNowException(
                    ServiceNowFailureKind.BudgetExpired,
                    "The ServiceNow turn budget expired.",
                    innerException: exception);
                ServiceNowDiagnostics.RecordFailure(failure.Kind);
                return GetStaleOrThrow(key, settings, failure);
            }
            catch (ServiceNowException exception)
            {
                ServiceNowDiagnostics.RecordFailure(exception.Kind);
                return GetStaleOrThrow(key, settings, exception);
            }
            catch (BrokenCircuitException exception)
            {
                var failure = new ServiceNowException(
                    ServiceNowFailureKind.CircuitOpen,
                    "The ServiceNow search circuit is open.",
                    innerException: exception);
                ServiceNowDiagnostics.RecordFailure(failure.Kind);
                return GetStaleOrThrow(key, settings, failure);
            }
            catch (TimeoutRejectedException exception)
            {
                var failure = new ServiceNowException(
                    ServiceNowFailureKind.Unavailable,
                    "A ServiceNow search attempt timed out.",
                    innerException: exception);
                ServiceNowDiagnostics.RecordFailure(failure.Kind);
                return GetStaleOrThrow(key, settings, failure);
            }
            catch (HttpRequestException exception)
            {
                var failure = new ServiceNowException(
                    ServiceNowFailureKind.Unavailable,
                    "ServiceNow AI Search is temporarily unavailable.",
                    innerException: exception);
                ServiceNowDiagnostics.RecordFailure(failure.Kind);
                return GetStaleOrThrow(key, settings, failure);
            }
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested && turn?.IsExpired == true)
        {
            var failure = new ServiceNowException(
                ServiceNowFailureKind.BudgetExpired,
                "The ServiceNow turn budget expired while waiting for an in-flight search.",
                innerException: exception);
            ServiceNowDiagnostics.RecordFailure(failure.Kind);
            return GetStaleOrThrow(key, options.Value, failure);
        }
        finally
        {
            ServiceNowDiagnostics.RecordLogicalDuration(timeProvider.GetElapsedTime(started), ServiceNowSearchSource.Live);
        }
    }

    private async Task<ServiceNowSearchResponse> SearchLiveAsync(
        string upn,
        string query,
        ServiceNowOptions settings,
        CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        using var firstResponse = await SendAsync(upn, query, token, cancellationToken);
        if (firstResponse.StatusCode != HttpStatusCode.Unauthorized)
        {
            return await ReadResponseAsync(firstResponse, settings, cancellationToken);
        }

        tokenProvider.Invalidate();
        token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        using var retryResponse = await SendAsync(upn, query, token, cancellationToken);
        return await ReadResponseAsync(retryResponse, settings, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string upn,
        string query,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var relativePath = $"{settings.SearchPath}?upn={Uri.EscapeDataString(upn)}&q={Uri.EscapeDataString(query)}";
        var requestUri = new Uri(new Uri(settings.Endpoint, UriKind.Absolute), relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var started = timeProvider.GetTimestamp();
        ServiceNowDiagnostics.RecordAttempt("search");
        try
        {
            return await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        finally
        {
            ServiceNowDiagnostics.RecordAttempt(timeProvider.GetElapsedTime(started), "search");
        }
    }

    private async Task<ServiceNowSearchResponse> ReadResponseAsync(
        HttpResponseMessage response,
        ServiceNowOptions settings,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure(response);
        }

        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            payloadWriter.Write("RAW SERVICENOW AI SEARCH RESPONSE", content);
            using var document = JsonDocument.Parse(content);
            return Parse(document.RootElement, settings);
        }
        catch (JsonException)
        {
            throw new ServiceNowException(ServiceNowFailureKind.InvalidResponse, "ServiceNow returned malformed search results.");
        }
    }

    internal static ServiceNowSearchResponse Parse(JsonElement root, ServiceNowOptions settings)
    {
        if (!root.TryGetProperty("result", out var result) ||
            !TryGetStatus(result, out var status) ||
            status != 200)
        {
            throw new ServiceNowException(ServiceNowFailureKind.InvalidResponse, "ServiceNow reported an unsuccessful search status.");
        }

        if (!result.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("search", out var search))
        {
            throw new ServiceNowException(ServiceNowFailureKind.InvalidResponse, "ServiceNow returned an incomplete search response.");
        }

        var term = Clean(GetString(search, "term"), settings.MaxSearchTermCharacters);
        var count = GetInt32(search, "count");
        var items = new List<ServiceNowSearchResult>();
        if (search.TryGetProperty("searchResults", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray().Take(settings.MaxResults))
            {
                items.Add(new ServiceNowSearchResult(
                    GetString(item, "sysId"),
                    GetString(item, "table"),
                    Clean(GetString(item, "title"), settings.MaxTitleCharacters),
                    Clean(GetString(item, "text"), settings.MaxTextCharacters),
                    ParseColumns(item, settings)));
            }
        }

        return new ServiceNowSearchResponse(term, count, items);
    }

    private static IReadOnlyList<ServiceNowSearchColumn> ParseColumns(JsonElement item, ServiceNowOptions settings)
    {
        var columns = new List<ServiceNowSearchColumn>();
        if (!item.TryGetProperty("columns", out var source) || source.ValueKind != JsonValueKind.Array)
        {
            return columns;
        }

        foreach (var column in source.EnumerateArray().Take(settings.MaxColumns))
        {
            columns.Add(new ServiceNowSearchColumn(
                Clean(GetString(column, "fieldName"), 200),
                Clean(GetString(column, "label"), 200),
                Clean(GetString(column, "value"), settings.MaxColumnValueCharacters),
                Clean(GetString(column, "displayValue"), settings.MaxColumnValueCharacters)));
        }

        return columns;
    }

    private static ServiceNowException CreateHttpFailure(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(ServiceNowFailureKind.Authentication, "ServiceNow rejected the OAuth access token."),
        HttpStatusCode.Forbidden => new(ServiceNowFailureKind.AccessDenied, "ServiceNow denied access to AI Search."),
        HttpStatusCode.TooManyRequests => new(
            ServiceNowFailureKind.RemoteThrottled,
            "ServiceNow AI Search is temporarily throttled.",
            GetRetryAfter(response)),
        _ when (int)response.StatusCode >= 500 => new(ServiceNowFailureKind.Unavailable, "ServiceNow AI Search is temporarily unavailable."),
        _ => new(ServiceNowFailureKind.InvalidResponse, $"ServiceNow AI Search returned HTTP {(int)response.StatusCode}.")
    };

    private ServiceNowSearchResponse? GetFresh(string key, ServiceNowOptions settings)
    {
        var entry = resultCache.Get(key);
        return entry is not null && timeProvider.GetUtcNow() - entry.RetrievedAt < TimeSpan.FromMinutes(settings.CacheFreshMinutes)
            ? entry.Response with { Source = ServiceNowSearchSource.FreshCache, RetrievedAt = entry.RetrievedAt }
            : null;
    }

    private ServiceNowSearchResponse GetStaleOrThrow(
        string key,
        ServiceNowOptions settings,
        ServiceNowException failure)
    {
        var entry = resultCache.Get(key);
        if (entry is not null && timeProvider.GetUtcNow() - entry.RetrievedAt <= TimeSpan.FromMinutes(settings.CacheMaximumStaleMinutes))
        {
            ServiceNowDiagnostics.RecordCache("stale");
            return entry.Response with { Source = ServiceNowSearchSource.StaleCache, RetrievedAt = entry.RetrievedAt };
        }

        throw failure;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is not { } date)
        {
            return null;
        }

        var duration = date - DateTimeOffset.UtcNow;
        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    private static bool TryGetStatus(JsonElement result, out int status)
    {
        status = 0;
        return result.TryGetProperty("status", out var property) &&
            (property.TryGetInt32(out status) ||
             (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out status)));
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.TryGetInt32(out var value)
                ? value
                : property.TryGetDouble(out var doubleValue) && doubleValue is >= 0 and <= int.MaxValue
                    ? (int)doubleValue
                    : 0
            : 0;
    }

    private static string Clean(string value, int maximumLength)
    {
        var decoded = WebUtility.HtmlDecode(value);
        var cleaned = new System.Text.StringBuilder(decoded.Length);
        var insideTag = false;
        foreach (var character in decoded)
        {
            if (character == '<')
            {
                insideTag = true;
                continue;
            }

            if (insideTag)
            {
                if (character == '>')
                {
                    insideTag = false;
                }

                continue;
            }

            if (!char.IsControl(character) || character is '\r' or '\n' or '\t')
            {
                cleaned.Append(character);
            }
        }

        var result = cleaned.ToString().Trim();
        return result.Length <= maximumLength ? result : result[..maximumLength];
    }
}
