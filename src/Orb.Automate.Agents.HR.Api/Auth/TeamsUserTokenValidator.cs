using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Auth;

/// <summary>
/// Validates delegated HR API tokens returned by the Azure Bot OAuth connection.
/// </summary>
public interface ITeamsUserTokenValidator
{
    /// <summary>Validates a token and returns its trusted identity.</summary>
    Task<TeamsUserContext?> ValidateAsync(string token, CancellationToken cancellationToken);
}

/// <summary>
/// Validates signature, issuer, audience, lifetime, scope and stable Entra identifiers.
/// </summary>
public sealed class TeamsUserTokenValidator : ITeamsUserTokenValidator
{
    private static readonly string[] UpnClaimTypes =
    [
        "preferred_username",
        "upn",
        ClaimTypes.Upn,
        ClaimTypes.Email,
        "email"
    ];

    private readonly EntraAuthenticationOptions _options;
    private readonly Func<CancellationToken, Task<OpenIdConnectConfiguration>> _getConfigurationAsync;
    private readonly ILogger<TeamsUserTokenValidator> _logger;

    /// <summary>Creates a validator for the configured tenant and HR API audience.</summary>
    public TeamsUserTokenValidator(
        IOptions<EntraAuthenticationOptions> options,
        ILogger<TeamsUserTokenValidator> logger)
    {
        _options = options.Value;
        _logger = logger;
        var metadataAddress = $"https://login.microsoftonline.com/{_options.TenantId}/v2.0/.well-known/openid-configuration";
        var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
        _getConfigurationAsync = configurationManager.GetConfigurationAsync;
    }

    internal TeamsUserTokenValidator(
        IOptions<EntraAuthenticationOptions> options,
        ILogger<TeamsUserTokenValidator> logger,
        Func<CancellationToken, Task<OpenIdConnectConfiguration>> getConfigurationAsync)
    {
        _options = options.Value;
        _logger = logger;
        _getConfigurationAsync = getConfigurationAsync;
    }

    /// <inheritdoc />
    public async Task<TeamsUserContext?> ValidateAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var configuration = await _getConfigurationAsync(cancellationToken);
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
                RequireSignedTokens = true,
                ValidateIssuer = true,
                ValidIssuer = $"https://login.microsoftonline.com/{_options.TenantId}/v2.0",
                ValidateAudience = true,
                ValidAudience = _options.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                NameClaimType = "preferred_username",
            };

            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler
            {
                MapInboundClaims = false,
            };
            var principal = handler.ValidateToken(token, validationParameters, out _);

            var tenantId = principal.FindFirst("tid")?.Value;
            var objectId = principal.FindFirst("oid")?.Value;
            if (!Guid.TryParse(tenantId, out _) ||
                !Guid.TryParse(objectId, out _) ||
                !string.Equals(tenantId, _options.TenantId, StringComparison.OrdinalIgnoreCase) ||
                !HasRequiredScope(principal, _options.RequiredScope))
            {
                return null;
            }

            var upn = UpnClaimTypes
                .Select(type => principal.FindFirst(type)?.Value?.Trim())
                .FirstOrDefault(IsValidUpn);

            return new TeamsUserContext(token, tenantId, objectId, upn);
        }
        catch (Exception exception) when (exception is SecurityTokenException or IOException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Teams delegated user token validation failed");
            return null;
        }
    }

    private static bool HasRequiredScope(ClaimsPrincipal principal, string requiredScope) =>
        principal.FindAll("scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(requiredScope, StringComparer.Ordinal);

    private static bool IsValidUpn(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 320 &&
        value.Contains('@', StringComparison.Ordinal);
}
