using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Auth;

/// <summary>
/// Resolves a search-scoped user token. In Development the developer's own Azure CLI identity is
/// used directly. Outside Development the access token accepted by the validated Entra bearer
/// handler is exchanged on behalf of the user.
/// </summary>
/// <param name="httpContextAccessor">Provides access to the inbound request.</param>
/// <param name="teamsUserContextAccessor">Provides a validated delegated assertion during Teams turns.</param>
/// <param name="environment">The host environment.</param>
/// <param name="credential">The ambient Azure credential, used only in Development.</param>
/// <param name="options">The on-behalf-of configuration.</param>
/// <param name="cache">Caches exchanged tokens until shortly before expiry.</param>
/// <param name="timeProvider">Supplies the current time.</param>
/// <param name="logger">The logger used for identity resolution events.</param>
public sealed class SearchUserTokenProvider(
    IHttpContextAccessor httpContextAccessor,
    ITeamsUserContextAccessor teamsUserContextAccessor,
    IHostEnvironment environment,
    TokenCredential credential,
    IOptions<OnBehalfOfOptions> options,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<SearchUserTokenProvider> logger) : ISearchUserTokenProvider
{
    private const string SearchScope = "https://search.azure.com/.default";
    private const string TokenExchangeScope = "api://AzureADTokenExchange/.default";
    private const string BearerPrefix = "Bearer ";
    private const string CacheKeyPrefix = "search-user-token:";

    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private static readonly Action<ILogger, Exception?> LogMissingAssertion =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(1, nameof(GetTokenAsync)),
            "No inbound bearer token is present, so SharePoint knowledge cannot be queried");

    private static readonly Action<ILogger, Exception?> LogExchangeFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, nameof(GetTokenAsync)),
            "Acquiring a search-scoped user token failed");

    private readonly ManagedIdentityCredential managedIdentity = new(ManagedIdentityId.SystemAssigned);

    /// <inheritdoc />
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        var assertion = teamsUserContextAccessor.Current?.AccessToken;
        if (assertion is null && environment.IsDevelopment())
        {
            return await AcquireAsync(credential, cancellationToken);
        }

        assertion ??= await GetValidatedAccessTokenAsync(httpContextAccessor.HttpContext);
        if (assertion is null)
        {
            LogMissingAssertion(logger, null);
            return null;
        }

        var cacheKey = CreateCacheKey(assertion);
        if (cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        var settings = options.Value;
        var onBehalfOf = new OnBehalfOfCredential(
            settings.TenantId,
            settings.ClientId,
            GetClientAssertionAsync,
            assertion);

        var token = await AcquireTokenAsync(onBehalfOf, cancellationToken);
        if (token is null)
        {
            return null;
        }

        var remaining = token.Value.ExpiresOn - timeProvider.GetUtcNow();
        var lifetime = remaining - ExpiryBuffer;
        if (lifetime > TimeSpan.Zero)
        {
            cache.Set(cacheKey, token.Value.Token, lifetime);
        }

        return token.Value.Token;
    }

    /// <summary>
    /// Extracts a bearer token from a request header. This exists for isolated parsing tests only;
    /// production OBO uses <see cref="GetValidatedAccessTokenAsync"/>.
    /// </summary>
    /// <param name="context">The current request, if any.</param>
    /// <returns>The token, or <see langword="null"/> when no usable bearer token is present.</returns>
    internal static string? ReadBearerToken(HttpContext? context)
    {
        var header = context?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = header[BearerPrefix.Length..].Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static async Task<string?> GetValidatedAccessTokenAsync(HttpContext? context)
    {
        return context?.User.Identity?.IsAuthenticated == true
            ? await context.GetTokenAsync(AuthenticationSchemes.HrApi, "access_token")
            : null;
    }

    private static string CreateCacheKey(string assertion)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(assertion));
        return string.Concat(CacheKeyPrefix, Convert.ToHexString(hash));
    }

    private async Task<string?> AcquireAsync(TokenCredential tokenCredential, CancellationToken cancellationToken)
    {
        var token = await AcquireTokenAsync(tokenCredential, cancellationToken);
        return token?.Token;
    }

    private async Task<AccessToken?> AcquireTokenAsync(
        TokenCredential tokenCredential,
        CancellationToken cancellationToken)
    {
        try
        {
            return await tokenCredential.GetTokenAsync(
                new TokenRequestContext([SearchScope]),
                cancellationToken);
        }
        catch (AuthenticationFailedException exception)
        {
            LogExchangeFailed(logger, exception);
            return null;
        }
    }

    private async Task<string> GetClientAssertionAsync(CancellationToken cancellationToken)
    {
        var token = await managedIdentity.GetTokenAsync(
            new TokenRequestContext([TokenExchangeScope]),
            cancellationToken);
        return token.Token;
    }
}
