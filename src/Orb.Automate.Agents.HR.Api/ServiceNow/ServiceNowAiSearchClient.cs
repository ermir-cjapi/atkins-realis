using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;

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
/// HTTP implementation of the ServiceNow AI Search client.
/// </summary>
public sealed class ServiceNowAiSearchClient(
    IHttpClientFactory httpClientFactory,
    IServiceNowOAuthTokenProvider tokenProvider,
    IOptions<ServiceNowOptions> options,
    DevelopmentPayloadWriter payloadWriter,
    ILogger<ServiceNowAiSearchClient> logger) : IServiceNowAiSearchClient
{
    internal const string HttpClientName = "ServiceNowAiSearch";
    private static readonly Action<ILogger, string, Exception?> LogRawSearchResponse =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1, nameof(SearchAsync)),
            "Raw ServiceNow AI Search response: {AiSearchResponse}");

    /// <inheritdoc />
    public async Task<ServiceNowSearchResponse> SearchAsync(
        string upn,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upn);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var settings = options.Value;
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length > settings.MaxSearchTermCharacters)
        {
            throw new ArgumentException(
                $"Search terms cannot exceed {settings.MaxSearchTermCharacters} characters.",
                nameof(query));
        }

        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        using var firstResponse = await SendAsync(upn, normalizedQuery, token, cancellationToken);
        if (firstResponse.StatusCode != HttpStatusCode.Unauthorized)
        {
            return await ReadResponseAsync(firstResponse, settings, cancellationToken);
        }

        tokenProvider.Invalidate();
        token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        using var retryResponse = await SendAsync(upn, normalizedQuery, token, cancellationToken);
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
        return await httpClientFactory.CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<ServiceNowSearchResponse> ReadResponseAsync(
        HttpResponseMessage response,
        ServiceNowOptions settings,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure(response.StatusCode);
        }

        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            LogRawSearchResponse(logger, content, null);
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

    private static ServiceNowException CreateHttpFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new(ServiceNowFailureKind.Authentication, "ServiceNow rejected the OAuth access token."),
        HttpStatusCode.Forbidden => new(ServiceNowFailureKind.AccessDenied, "ServiceNow denied access to AI Search."),
        HttpStatusCode.TooManyRequests => new(ServiceNowFailureKind.Throttled, "ServiceNow AI Search is temporarily throttled."),
        _ when (int)statusCode >= 500 => new(ServiceNowFailureKind.Unavailable, "ServiceNow AI Search is temporarily unavailable."),
        _ => new(ServiceNowFailureKind.InvalidResponse, $"ServiceNow AI Search returned HTTP {(int)statusCode}.")
    };

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
