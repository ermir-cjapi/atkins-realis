using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Auth;

/// <summary>The user dimensions required by ARIA business events.</summary>
public sealed record CurrentUserProfile(string UserId, string Country, string Department)
{
    /// <summary>Creates a profile with unavailable Graph dimensions.</summary>
    public static CurrentUserProfile Unknown(string userId) => new(userId, "unknown", "unknown");
}

/// <summary>Resolves the authenticated user's telemetry profile.</summary>
public interface ICurrentUserProfileProvider
{
    /// <summary>Gets the current user's profile, using controlled fallback values when unavailable.</summary>
    Task<CurrentUserProfile> GetCurrentAsync(CancellationToken cancellationToken);
}

/// <summary>Resolves the approved raw UPN and enriches it from Microsoft Graph using an OBO token.</summary>
public sealed class CurrentUserProfileProvider(
    ICurrentUserUpnAccessor currentUser,
    IHttpContextAccessor httpContextAccessor,
    IHttpClientFactory httpClientFactory,
    IHostEnvironment environment,
    TokenCredential developerCredential,
    IOptions<OnBehalfOfOptions> onBehalfOfOptions,
    IOptions<GraphProfileOptions> graphOptions,
    IMemoryCache cache,
    ILogger<CurrentUserProfileProvider> logger) : ICurrentUserProfileProvider
{
    private const string GraphScope = "https://graph.microsoft.com/User.Read";
    private const string TokenExchangeScope = "api://AzureADTokenExchange/.default";
    private const string CachePrefix = "graph-profile:";
    private readonly ManagedIdentityCredential managedIdentity = new(ManagedIdentityId.SystemAssigned);

    /// <inheritdoc />
    public async Task<CurrentUserProfile> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var upn = currentUser.GetCurrentUserUpn() ?? "unknown";
        var settings = graphOptions.Value;
        if (!settings.Enabled || upn == "unknown")
        {
            return CurrentUserProfile.Unknown(upn);
        }

        var cacheKey = string.Concat(CachePrefix, upn.ToUpperInvariant());
        if (cache.TryGetValue(cacheKey, out CurrentUserProfile? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var token = onBehalfOfOptions.Value.UserTokenMode == SearchUserTokenMode.AzureCli
                ? await developerCredential.GetTokenAsync(new TokenRequestContext([GraphScope]), cancellationToken)
                : await GetOnBehalfOfTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token.Token))
            {
                return CurrentUserProfile.Unknown(upn);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "me?$select=userPrincipalName,usageLocation,department");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await httpClientFactory.CreateClient(nameof(CurrentUserProfileProvider))
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<GraphProfileResponse>(cancellationToken);

            // Locally the developer credential belongs to the signed-in engineer rather than to a
            // configured test UPN. Attributing that profile to another identity would publish
            // incorrect country and department dimensions, so it fails closed instead.
            if (!string.Equals(payload?.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase))
            {
                LogIdentityMismatch(logger, null);
                return CurrentUserProfile.Unknown(upn);
            }

            var profile = new CurrentUserProfile(upn, Normalize(payload?.UsageLocation), Normalize(payload?.Department));
            cache.Set(cacheKey, profile, TimeSpan.FromMinutes(settings.CacheMinutes));
            return profile;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Microsoft Graph profile enrichment failed");
            return CurrentUserProfile.Unknown(upn);
        }
    }

    private async Task<AccessToken> GetOnBehalfOfTokenAsync(CancellationToken cancellationToken)
    {
        var assertion = await GetValidatedAccessTokenAsync(httpContextAccessor.HttpContext);
        if (assertion is null)
        {
            return default;
        }

        var settings = onBehalfOfOptions.Value;
        var credential = environment.IsDevelopment()
            ? new OnBehalfOfCredential(settings.TenantId, settings.ClientId, settings.ClientSecret, assertion)
            : new OnBehalfOfCredential(settings.TenantId, settings.ClientId, GetClientAssertionAsync, assertion);
        return await credential.GetTokenAsync(new TokenRequestContext([GraphScope]), cancellationToken);
    }

    private static async Task<string?> GetValidatedAccessTokenAsync(HttpContext? context) =>
        context?.User.Identity?.IsAuthenticated == true
            ? await context.GetTokenAsync("access_token")
            : null;

    private async Task<string> GetClientAssertionAsync(CancellationToken cancellationToken)
    {
        var token = await managedIdentity.GetTokenAsync(new TokenRequestContext([TokenExchangeScope]), cancellationToken);
        return token.Token;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static readonly Action<ILogger, Exception?> LogIdentityMismatch =
        LoggerMessage.Define(LogLevel.Debug, new EventId(1, nameof(GetCurrentAsync)),
            "Graph profile belongs to a different identity than the current chat user; dimensions remain unknown");

    private sealed record GraphProfileResponse(string? UserPrincipalName, string? UsageLocation, string? Department);
}
