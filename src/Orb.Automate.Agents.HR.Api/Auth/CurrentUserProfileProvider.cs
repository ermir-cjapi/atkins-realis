using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Auth;

/// <summary>The user dimensions required by Assist | HR business events.</summary>
public sealed record CurrentUserProfile(
    string UserId, string Country, string Department, string Company, string Division, string CostCenter)
{
    /// <summary>Creates a profile with unavailable Graph dimensions.</summary>
    public static CurrentUserProfile Unknown(string userId) =>
        new(userId, "unknown", "unknown", "unknown", "unknown", "unknown");
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
    ITeamsUserContextAccessor teamsUserContextAccessor,
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
    private const string ObjectIdClaimType = "oid";
    private const string MappedObjectIdClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";
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

            using var request = new HttpRequestMessage(HttpMethod.Get, "me?$select=id,userPrincipalName,usageLocation,department,companyName,employeeOrgData");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await httpClientFactory.CreateClient(nameof(CurrentUserProfileProvider))
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<GraphProfileResponse>(cancellationToken);

            // Locally the developer credential belongs to the signed-in engineer rather than to a
            // configured test UPN. Attributing that profile to another identity would publish
            // incorrect organisational dimensions, so it fails closed instead. The immutable Entra
            // object ID is compared when the caller's token supplied one; UPNs can legitimately
            // differ (aliases, renames, guest formats), so they are only a fallback.
            var verifiedObjectId = GetVerifiedObjectId();
            var identityMatches = verifiedObjectId is not null
                ? string.Equals(payload?.Id, verifiedObjectId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(payload?.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase);
            if (!identityMatches)
            {
                LogIdentityMismatch(logger, verifiedObjectId is not null ? "object ID" : "UPN", null);
                return CurrentUserProfile.Unknown(upn);
            }

            var profile = new CurrentUserProfile(
                upn,
                Normalize(payload?.UsageLocation),
                Normalize(payload?.Department),
                Normalize(payload?.CompanyName),
                Normalize(payload?.EmployeeOrgData?.Division),
                Normalize(payload?.EmployeeOrgData?.CostCenter));
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
        // Teams turns run outside the HTTP request, so prefer the validated Teams SSO assertion.
        var teamsAssertion = teamsUserContextAccessor.Current?.AccessToken;
        var assertion = teamsAssertion
            ?? await GetValidatedAccessTokenAsync(httpContextAccessor.HttpContext);
        if (assertion is null)
        {
            logger.LogWarning("No delegated assertion is available for the Microsoft Graph on-behalf-of exchange");
            return default;
        }

        logger.LogInformation(
            "Exchanging the {AssertionSource} delegated assertion on behalf of the user for Microsoft Graph",
            teamsAssertion is not null ? "Teams SSO" : "HR API bearer");

        var settings = onBehalfOfOptions.Value;
        var credential = environment.IsDevelopment()
            ? new OnBehalfOfCredential(settings.TenantId, settings.ClientId, settings.ClientSecret, assertion)
            : new OnBehalfOfCredential(settings.TenantId, settings.ClientId, GetClientAssertionAsync, assertion);
        return await credential.GetTokenAsync(new TokenRequestContext([GraphScope]), cancellationToken);
    }

    private string? GetVerifiedObjectId()
    {
        var teamsObjectId = teamsUserContextAccessor.Current?.UserObjectId;
        if (!string.IsNullOrWhiteSpace(teamsObjectId))
        {
            return teamsObjectId.Trim();
        }

        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated is not true ||
            principal.HasClaim(AuthenticationSchemes.PrincipalSourceClaim, AuthenticationSchemes.BotConnector))
        {
            return null;
        }

        var objectId = (principal.FindFirst(ObjectIdClaimType) ?? principal.FindFirst(MappedObjectIdClaimType))?.Value;
        return string.IsNullOrWhiteSpace(objectId) ? null : objectId.Trim();
    }

    private static async Task<string?> GetValidatedAccessTokenAsync(HttpContext? context) =>
        context?.User.Identity?.IsAuthenticated == true
            ? await context.GetTokenAsync(AuthenticationSchemes.HrApi, "access_token")
            : null;

    private async Task<string> GetClientAssertionAsync(CancellationToken cancellationToken)
    {
        var token = await managedIdentity.GetTokenAsync(new TokenRequestContext([TokenExchangeScope]), cancellationToken);
        return token.Token;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static readonly Action<ILogger, string, Exception?> LogIdentityMismatch =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(GetCurrentAsync)),
            "Graph profile belongs to a different identity than the current chat user (compared on {IdentityComparison}); dimensions remain unknown");

    private sealed record GraphProfileResponse(
        string? Id,
        string? UserPrincipalName,
        string? UsageLocation,
        string? Department,
        string? CompanyName,
        GraphEmployeeOrgData? EmployeeOrgData);

    private sealed record GraphEmployeeOrgData(string? Division, string? CostCenter);
}
