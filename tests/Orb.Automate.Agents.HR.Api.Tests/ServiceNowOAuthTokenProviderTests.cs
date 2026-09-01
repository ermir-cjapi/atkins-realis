using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests ServiceNow OAuth token acquisition and caching.
/// </summary>
public sealed class ServiceNowOAuthTokenProviderTests
{
    /// <summary>
    /// The provider posts client credentials and reuses the returned token.
    /// </summary>
    [Fact]
    public async Task GetsAndCachesClientCredentialsToken()
    {
        var calls = 0;
        string? form = null;
        var handler = new DelegateHttpMessageHandler(async (request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.service-now.com/oauth_token.do", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            var credentials = Encoding.ASCII.GetString(
                Convert.FromBase64String(request.Headers.Authorization?.Parameter ?? string.Empty));
            Assert.Equal("client-id:client-secret", credentials);
            form = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{\"access_token\":\"token-value\",\"expires_in\":3600}");
        });
        using var provider = CreateProvider(handler);

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("token-value", first);
        Assert.Equal(first, second);
        Assert.Equal(1, calls);
        Assert.Contains("grant_type=client_credentials", form, StringComparison.Ordinal);
        Assert.DoesNotContain("client_id", form, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", form, StringComparison.Ordinal);
    }

    /// <summary>
    /// Invalidating the cache forces a new token acquisition.
    /// </summary>
    [Fact]
    public async Task InvalidateForcesRefresh()
    {
        var calls = 0;
        var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse($"{{\"access_token\":\"token-{calls}\",\"expires_in\":\"3600\"}}"));
        });
        using var provider = CreateProvider(handler);

        Assert.Equal("token-1", await provider.GetAccessTokenAsync(CancellationToken.None));
        provider.Invalidate();
        Assert.Equal("token-2", await provider.GetAccessTokenAsync(CancellationToken.None));
    }

    /// <summary>
    /// Concurrent callers share one token refresh rather than causing a request stampede.
    /// </summary>
    [Fact]
    public async Task ConcurrentCallersShareSingleRefresh()
    {
        var calls = 0;
        var handler = new DelegateHttpMessageHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return JsonResponse("{\"access_token\":\"shared-token\",\"expires_in\":3600}");
        });
        using var provider = CreateProvider(handler);

        var tokens = await Task.WhenAll(
            Enumerable.Range(0, 10)
                .Select(_ => provider.GetAccessTokenAsync(CancellationToken.None)));

        Assert.All(tokens, token => Assert.Equal("shared-token", token));
        Assert.Equal(1, calls);
    }

    private static ServiceNowOAuthTokenProvider CreateProvider(HttpMessageHandler handler) =>
        new(
            new StubHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new ServiceNowOptions
            {
                Enabled = true,
                Endpoint = "https://example.service-now.com",
                TokenEndpoint = "https://example.service-now.com/oauth_token.do",
                ClientId = "client-id",
                ClientSecret = "client-secret"
            }),
            TimeProvider.System);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
