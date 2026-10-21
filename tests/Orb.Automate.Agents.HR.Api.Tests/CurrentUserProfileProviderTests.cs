using System.Net;
using System.Security.Claims;
using System.Text;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests the identity check applied to the Microsoft Graph profile.</summary>
public sealed class CurrentUserProfileProviderTests
{
    private const string Upn = "employee@example.com";
    private const string ObjectId = "11111111-1111-1111-1111-111111111111";

    /// <summary>Verifies that a matching object ID is accepted even when the UPN differs.</summary>
    [Fact]
    public async Task GetCurrentAsync_AcceptsMatchingObjectIdWhenUpnDiffers()
    {
        var teamsContext = new TeamsUserContextAccessor();
        var provider = CreateProvider(GraphJson(ObjectId, "alias@example.com"), teamsContext);

        using (teamsContext.Push(new TeamsUserContext("assertion", Guid.NewGuid().ToString(), ObjectId, Upn)))
        {
            var profile = await provider.GetCurrentAsync(CancellationToken.None);

            Assert.Equal("GB", profile.Country);
        }
    }

    /// <summary>Verifies that a different object ID fails closed even when the UPN matches.</summary>
    [Fact]
    public async Task GetCurrentAsync_RejectsDifferentObjectIdWhenUpnMatches()
    {
        var teamsContext = new TeamsUserContextAccessor();
        var provider = CreateProvider(GraphJson(Guid.NewGuid().ToString(), Upn), teamsContext);

        using (teamsContext.Push(new TeamsUserContext("assertion", Guid.NewGuid().ToString(), ObjectId, Upn)))
        {
            var profile = await provider.GetCurrentAsync(CancellationToken.None);

            Assert.Equal("unknown", profile.Country);
        }
    }

    /// <summary>Verifies that the HR API bearer's mapped object ID claim is used.</summary>
    [Fact]
    public async Task GetCurrentAsync_UsesHrApiObjectIdClaim()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", ObjectId)],
                "Bearer"))
        };
        var provider = CreateProvider(
            GraphJson(ObjectId, "alias@example.com"),
            new TeamsUserContextAccessor(),
            new HttpContextAccessor { HttpContext = httpContext });

        var profile = await provider.GetCurrentAsync(CancellationToken.None);

        Assert.Equal("GB", profile.Country);
    }

    /// <summary>Verifies the UPN comparison is used when no object ID has been verified.</summary>
    /// <param name="graphUpn">The UPN returned by Graph.</param>
    /// <param name="expectedCountry">The expected profile country.</param>
    [Theory]
    [InlineData(Upn, "GB")]
    [InlineData("EMPLOYEE@example.com", "GB")]
    [InlineData("someone.else@example.com", "unknown")]
    public async Task GetCurrentAsync_FallsBackToUpnWithoutVerifiedObjectId(string graphUpn, string expectedCountry)
    {
        var provider = CreateProvider(GraphJson(Guid.NewGuid().ToString(), graphUpn), new TeamsUserContextAccessor());

        var profile = await provider.GetCurrentAsync(CancellationToken.None);

        Assert.Equal(expectedCountry, profile.Country);
    }

    private static string GraphJson(string id, string upn) =>
        $$"""{"id":"{{id}}","userPrincipalName":"{{upn}}","usageLocation":"GB","department":"HR"}""";

    private static CurrentUserProfileProvider CreateProvider(
        string graphJson,
        ITeamsUserContextAccessor teamsContext,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(graphJson, Encoding.UTF8, "application/json")
        }));
        var httpClientFactory = new BaseAddressHttpClientFactory(handler);

        return new CurrentUserProfileProvider(
            new StubUpnAccessor(Upn),
            httpContextAccessor ?? new HttpContextAccessor(),
            teamsContext,
            httpClientFactory,
            new TestHostEnvironment("Development"),
            new StubCredential(),
            Microsoft.Extensions.Options.Options.Create(new OnBehalfOfOptions { UserTokenMode = SearchUserTokenMode.AzureCli }),
            Microsoft.Extensions.Options.Options.Create(new GraphProfileOptions { Enabled = true }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CurrentUserProfileProvider>.Instance);
    }

    private sealed class BaseAddressHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://graph.example.test/v1.0/") };
    }

    private sealed class StubUpnAccessor(string upn) : ICurrentUserUpnAccessor
    {
        public string? GetCurrentUserUpn() => upn;
    }

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("graph-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
