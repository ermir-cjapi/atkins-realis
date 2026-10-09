using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests how the inbound user assertion is read from a request. A defect here silently disables
/// permission trimming, so the accepted and rejected shapes are both pinned.
/// </summary>
public sealed class SearchUserTokenProviderTests
{
    /// <summary>
    /// Verifies that no identity is reported when there is no request in scope.
    /// </summary>
    [Fact]
    public void ReadBearerToken_ReturnsNullWhenThereIsNoRequest() =>
        Assert.Null(SearchUserTokenProvider.ReadBearerToken(null));

    /// <summary>
    /// Verifies that a bearer token is extracted regardless of scheme casing or padding.
    /// </summary>
    /// <param name="header">The inbound header value.</param>
    /// <param name="expected">The expected token.</param>
    [Theory]
    [InlineData("Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("Bearer   abc.def.ghi  ", "abc.def.ghi")]
    public void ReadBearerToken_ExtractsTheToken(string header, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = header;

        Assert.Equal(expected, SearchUserTokenProvider.ReadBearerToken(context));
    }

    /// <summary>
    /// Verifies that headers which cannot yield a bearer token are rejected rather than forwarded.
    /// </summary>
    /// <param name="header">The inbound header value.</param>
    [Theory]
    [InlineData("")]
    [InlineData("Basic dXNlcjpwYXNzd29yZA==")]
    [InlineData("Bearer")]
    [InlineData("Bearer   ")]
    public void ReadBearerToken_RejectsUnusableHeaders(string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = header;

        Assert.Null(SearchUserTokenProvider.ReadBearerToken(context));
    }

    /// <summary>
    /// Verifies that Development uses the ambient developer credential outside a Teams turn.
    /// </summary>
    [Fact]
    public async Task GetTokenAsync_UsesAmbientCredentialInDevelopment()
    {
        var credential = new StubCredential("cli-token");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(Environments.Development, credential, cache, new TeamsUserContextAccessor());

        var token = await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal("cli-token", token);
        Assert.Equal(1, credential.Calls);
    }

    /// <summary>
    /// Verifies that deployed environments fail closed when no validated user assertion exists.
    /// </summary>
    [Fact]
    public async Task GetTokenAsync_ReturnsNullWithoutAssertionOutsideDevelopment()
    {
        var credential = new StubCredential("cli-token");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(Environments.Production, credential, cache, new TeamsUserContextAccessor());

        var token = await provider.GetTokenAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.Equal(0, credential.Calls);
    }

    /// <summary>
    /// Verifies that a validated Teams SSO assertion is exchanged instead of the ambient credential,
    /// including in Development.
    /// </summary>
    /// <param name="environmentName">The host environment name.</param>
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task GetTokenAsync_UsesTeamsAssertionDuringTeamsTurn(string environmentName)
    {
        const string assertion = "teams-sso-assertion";
        var credential = new StubCredential("cli-token");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(CreateCacheKey(assertion), "exchanged-search-token");
        var teamsContext = new TeamsUserContextAccessor();
        var provider = CreateProvider(environmentName, credential, cache, teamsContext);

        using (teamsContext.Push(new TeamsUserContext(
            assertion,
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            "employee@example.com")))
        {
            var token = await provider.GetTokenAsync(CancellationToken.None);

            Assert.Equal("exchanged-search-token", token);
        }

        Assert.Equal(0, credential.Calls);
    }

    private static SearchUserTokenProvider CreateProvider(
        string environmentName,
        TokenCredential credential,
        IMemoryCache cache,
        ITeamsUserContextAccessor teamsContext) =>
        new(
            new HttpContextAccessor(),
            teamsContext,
            new TestHostEnvironment { EnvironmentName = environmentName },
            credential,
            Microsoft.Extensions.Options.Options.Create(new OnBehalfOfOptions
            {
                TenantId = Guid.NewGuid().ToString(),
                ClientId = Guid.NewGuid().ToString(),
            }),
            cache,
            TimeProvider.System,
            NullLogger<SearchUserTokenProvider>.Instance);

    private static string CreateCacheKey(string assertion) =>
        "search-user-token:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assertion)));

    private sealed class StubCredential(string token) : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
