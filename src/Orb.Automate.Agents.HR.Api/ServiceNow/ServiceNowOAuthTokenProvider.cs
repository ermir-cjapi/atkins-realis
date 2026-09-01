using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.ServiceNow;

/// <summary>
/// Supplies cached OAuth access tokens for ServiceNow.
/// </summary>
public interface IServiceNowOAuthTokenProvider
{
    /// <summary>
    /// Gets a valid access token.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Invalidates the cached token after an authentication rejection.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// Uses the OAuth 2.0 client-credentials flow and caches tokens until shortly before expiry.
/// </summary>
public sealed class ServiceNowOAuthTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<ServiceNowOptions> options,
    TimeProvider timeProvider) : IServiceNowOAuthTokenProvider, IDisposable
{
    internal const string HttpClientName = "ServiceNowOAuth";
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CachedToken? _cachedToken;

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _cachedToken);
        if (IsUsable(cached))
        {
            return cached!.AccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            cached = Volatile.Read(ref _cachedToken);
            if (IsUsable(cached))
            {
                return cached!.AccessToken;
            }

            var settings = options.Value;
            if (!settings.Enabled)
            {
                throw new ServiceNowException(ServiceNowFailureKind.Configuration, "ServiceNow search is not enabled.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials"
                })
            };
            var credentials = Encoding.ASCII.GetBytes($"{settings.ClientId}:{settings.ClientSecret}");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(credentials));

            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceNowException(
                    ServiceNowFailureKind.Authentication,
                    $"ServiceNow OAuth rejected the client credentials with HTTP {(int)response.StatusCode}.");
            }

            OAuthTokenResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new ServiceNowException(ServiceNowFailureKind.InvalidResponse, "ServiceNow returned an invalid OAuth response.");
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken) || !TryGetLifetime(payload.ExpiresIn, out var lifetime))
            {
                throw new ServiceNowException(ServiceNowFailureKind.InvalidResponse, "ServiceNow returned an incomplete OAuth response.");
            }

            var token = new CachedToken(payload.AccessToken, timeProvider.GetUtcNow().Add(lifetime));
            Volatile.Write(ref _cachedToken, token);
            return token.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate() => Volatile.Write(ref _cachedToken, null);

    /// <inheritdoc />
    public void Dispose() => _refreshLock.Dispose();

    private bool IsUsable(CachedToken? token) =>
        token is not null && token.ExpiresOn > timeProvider.GetUtcNow().Add(ExpirySkew);

    private static bool TryGetLifetime(JsonElement expiresIn, out TimeSpan lifetime)
    {
        var seconds = 0d;
        var validNumber = expiresIn.ValueKind == JsonValueKind.Number && expiresIn.TryGetDouble(out seconds);
        var validString = expiresIn.ValueKind == JsonValueKind.String &&
            double.TryParse(expiresIn.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
        if ((validNumber || validString) && seconds > 0)
        {
            lifetime = TimeSpan.FromSeconds(seconds);
            return true;
        }

        lifetime = default;
        return false;
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresOn);

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] JsonElement ExpiresIn);
}
