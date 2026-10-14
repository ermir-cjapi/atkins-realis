using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests fail-closed validation of delegated Teams SSO tokens.</summary>
public sealed class TeamsUserTokenValidatorTests : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly string _tenantId = Guid.NewGuid().ToString();
    private readonly string _audience = $"api://{Guid.NewGuid()}";

    /// <summary>Creates signing material for each test.</summary>
    public TeamsUserTokenValidatorTests()
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = Guid.NewGuid().ToString() };
    }

    /// <summary>A valid delegated HR API token produces trusted per-turn identity.</summary>
    [Fact]
    public async Task ValidTokenReturnsTrustedIdentity()
    {
        var objectId = Guid.NewGuid().ToString();
        var token = CreateToken(_tenantId, _audience, "access_as_user", objectId, "employee@example.com");

        var result = await CreateValidator().ValidateAsync(token, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(_tenantId, result.TenantId);
        Assert.Equal(objectId, result.UserObjectId);
        Assert.Equal("employee@example.com", result.Upn);
        Assert.Equal(token, result.AccessToken);
    }

    /// <summary>Tenant, audience, expiry, and scope failures are rejected.</summary>
    [Theory]
    [InlineData("tenant")]
    [InlineData("audience")]
    [InlineData("expiry")]
    [InlineData("scope")]
    public async Task InvalidTokenIsRejected(string failure)
    {
        var tenant = failure == "tenant" ? Guid.NewGuid().ToString() : _tenantId;
        var audience = failure == "audience" ? $"api://{Guid.NewGuid()}" : _audience;
        var scope = failure == "scope" ? "other_scope" : "access_as_user";
        var expires = failure == "expiry" ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(10);
        var token = CreateToken(tenant, audience, scope, Guid.NewGuid().ToString(), "employee@example.com", expires);

        var result = await CreateValidator().ValidateAsync(token, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>A UPN is optional at token-validation time and remains unavailable to ServiceNow.</summary>
    [Fact]
    public async Task MissingUpnProducesIdentityWithoutUpn()
    {
        var token = CreateToken(_tenantId, _audience, "access_as_user", Guid.NewGuid().ToString(), null);

        var result = await CreateValidator().ValidateAsync(token, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result.Upn);
    }

    /// <summary>The diagnostic summary records audience and scopes without exposing the token or user identity.</summary>
    [Fact]
    public async Task TokenSummaryIsLoggedWithoutSensitiveValues()
    {
        var objectId = Guid.NewGuid().ToString();
        var token = CreateToken(_tenantId, _audience, "other_scope", objectId, "employee@example.com");
        var logger = new CapturingLogger<TeamsUserTokenValidator>();

        var result = await CreateValidator(logger).ValidateAsync(token, CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(logger.Messages, message =>
            message.Contains($"Audience={_audience}", StringComparison.Ordinal) &&
            message.Contains("Scopes=other_scope", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message =>
            message.Contains("rejected: required scope missing", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message =>
            message.Contains(token, StringComparison.Ordinal) ||
            message.Contains(objectId, StringComparison.Ordinal) ||
            message.Contains("employee@example.com", StringComparison.Ordinal));
    }

    private TeamsUserTokenValidator CreateValidator(ILogger<TeamsUserTokenValidator>? logger = null)
    {
        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(_key);
        return new TeamsUserTokenValidator(
            Microsoft.Extensions.Options.Options.Create(new EntraAuthenticationOptions
            {
                TenantId = _tenantId,
                Audience = _audience,
                RequiredScope = "access_as_user",
            }),
            logger ?? NullLogger<TeamsUserTokenValidator>.Instance,
            _ => Task.FromResult(configuration));
    }

    private string CreateToken(
        string tenantId,
        string audience,
        string scope,
        string objectId,
        string? upn,
        DateTime? expires = null)
    {
        var claims = new List<Claim>
        {
            new("tid", tenantId),
            new("oid", objectId),
            new("scp", scope),
        };
        if (upn is not null)
        {
            claims.Add(new Claim("preferred_username", upn));
        }

        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0",
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = FailureSafeNotBefore(expires, now),
            IssuedAt = FailureSafeNotBefore(expires, now),
            Expires = expires ?? now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        };

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private static DateTime FailureSafeNotBefore(DateTime? expires, DateTime now) =>
        expires < now ? expires.Value.AddMinutes(-10) : now.AddMinutes(-1);

    /// <inheritdoc />
    public void Dispose() => _rsa.Dispose();

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
