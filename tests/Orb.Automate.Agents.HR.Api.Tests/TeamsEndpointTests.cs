using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using AuthenticationSchemes = Orb.Automate.Agents.HR.Api.Auth.AuthenticationSchemes;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests Bot Connector endpoint authentication and its separation from the delegated API.</summary>
public sealed class TeamsEndpointTests : IClassFixture<TeamsEndpointTests.TeamsApplication>
{
    private const string BotAppId = "00000000-0000-0000-0000-000000000001";
    private const string EntraTenantId = "00000000-0000-0000-0000-000000000000";
    private const string EntraAudience = "api://test-agent";
    private const string BotFrameworkIssuer = "https://api.botframework.com";
    private const string ServiceUrl = "https://smba.trafficmanager.net/emea/";

    private readonly TeamsApplication _application;
    private readonly HttpClient _client;

    /// <summary>Creates the Teams-enabled test client.</summary>
    public TeamsEndpointTests(TeamsApplication application)
    {
        _application = application;
        _client = application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    /// <summary>An unauthenticated caller cannot submit a bot activity.</summary>
    [Fact]
    public async Task MessagesRejectsMissingBotConnectorToken()
    {
        var response = await _client.PostAsJsonAsync("/api/messages", CreateActivity("message"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Tokens for another bot, another issuer, expired tokens and untrusted keys are rejected.</summary>
    [Theory]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expiry")]
    [InlineData("signature")]
    public async Task MessagesRejectsInvalidBotConnectorToken(string failure)
    {
        using var untrustedKey = RSA.Create(2048);
        var token = CreateToken(
            issuer: failure == "issuer" ? "https://login.microsoftonline.com/common/v2.0" : BotFrameworkIssuer,
            audience: failure == "audience" ? "00000000-0000-0000-0000-00000000000f" : BotAppId,
            expires: failure == "expiry" ? DateTime.UtcNow.AddMinutes(-10) : null,
            signingKey: failure == "signature" ? new RsaSecurityKey(untrustedKey) { KeyId = "untrusted" } : null,
            claims: [new Claim("serviceurl", ServiceUrl)]);

        var response = await SendAsync("/api/messages", token, CreateActivity("message"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A delegated HR API token cannot be replayed as a Bot Connector token.</summary>
    [Fact]
    public async Task MessagesRejectsDelegatedHrApiToken()
    {
        var token = CreateDelegatedToken();

        var response = await SendAsync("/api/messages", token, CreateActivity("message"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A Bot Connector token cannot authorize the direct chat API.</summary>
    [Fact]
    public async Task ChatRejectsBotConnectorToken()
    {
        var token = CreateToken(BotFrameworkIssuer, BotAppId, claims: [new Claim("serviceurl", ServiceUrl)]);

        var response = await SendAsync("/api/chat/", token, new { message = "How does sick leave work?" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A valid Bot Connector token for this bot is accepted by the endpoint.</summary>
    [Fact]
    public async Task MessagesAcceptsValidBotConnectorToken()
    {
        var token = CreateToken(BotFrameworkIssuer, BotAppId, claims: [new Claim("serviceurl", ServiceUrl)]);

        var response = await SendAsync("/api/messages", token, CreateActivity("conversationUpdate"));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.IsSuccessStatusCode, $"Unexpected status code {(int)response.StatusCode}.");
    }

    /// <summary>Teams hosting outside Development fails at startup without durable turn-state storage.</summary>
    [Fact]
    public void TeamsWithoutDurableStorageFailsAtStartup()
    {
        using var application = new TeamsApplication().WithWebHostBuilder(builder =>
            builder.UseSetting("Teams:StorageContainerUri", ""));

        var exception = Assert.ThrowsAny<Exception>(() => application.CreateClient());

        Assert.Contains("durable storage container URI", exception.ToString(), StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private string CreateDelegatedToken() => CreateToken(
        $"https://login.microsoftonline.com/{EntraTenantId}/v2.0",
        EntraAudience,
        claims:
        [
            new Claim("tid", EntraTenantId),
            new Claim("oid", Guid.NewGuid().ToString()),
            new Claim("scp", "access_as_user"),
            new Claim("preferred_username", "employee@example.com"),
        ]);

    private string CreateToken(
        string issuer,
        string audience,
        DateTime? expires = null,
        SecurityKey? signingKey = null,
        IEnumerable<Claim>? claims = null)
    {
        var now = DateTime.UtcNow;
        var notBefore = expires < now ? expires.Value.AddMinutes(-10) : now.AddMinutes(-1);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims ?? []),
            NotBefore = notBefore,
            IssuedAt = notBefore,
            Expires = expires ?? now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                signingKey ?? _application.SigningKey,
                SecurityAlgorithms.RsaSha256),
        };

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private static object CreateActivity(string type) => new
    {
        type,
        id = "activity-id",
        timestamp = DateTimeOffset.UtcNow,
        serviceUrl = ServiceUrl,
        channelId = "msteams",
        from = new { id = "user-id" },
        conversation = new { id = "conversation-id", tenantId = EntraTenantId },
        recipient = new { id = $"28:{BotAppId}" },
        text = type == "message" ? "hello" : null,
    };

    /// <summary>Test host with the Teams channel enabled and locally trusted signing keys.</summary>
    public sealed class TeamsApplication : WebApplicationFactory<Program>
    {
        private readonly RSA _rsa = RSA.Create(2048);

        /// <summary>Creates the test host signing key.</summary>
        public TeamsApplication() => SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-key" };

        /// <summary>Gets the key trusted by both bearer schemes in the test host.</summary>
        public RsaSecurityKey SigningKey { get; }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _rsa.Dispose();
            }
        }

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Teams:Enabled", "true");
            builder.UseSetting("Teams:AppId", BotAppId);
            builder.UseSetting("Teams:TenantId", "00000000-0000-0000-0000-000000000002");
            builder.UseSetting("Teams:StorageContainerUri", "https://example.blob.core.windows.net/agents-hr-authorization");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStorage, MemoryStorage>();
                ReplaceMetadata(services, AuthenticationSchemes.BotConnector, BotFrameworkIssuer);
                ReplaceMetadata(
                    services,
                    AuthenticationSchemes.HrApi,
                    $"https://login.microsoftonline.com/{EntraTenantId}/v2.0");
            });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Agent:Name"] = "Test Agent",
                    ["Foundry:Endpoint"] = "https://example.openai.azure.com/",
                    ["Foundry:DeploymentName"] = "test-deployment",
                    ["EntraAuthentication:TenantId"] = "00000000-0000-0000-0000-000000000000",
                    ["EntraAuthentication:Audience"] = "api://test-agent",
                    ["EntraAuthentication:RequiredScope"] = "access_as_user",
                    ["Connections:ServiceConnection:Assembly"] = "Microsoft.Agents.Authentication.Msal",
                    ["Connections:ServiceConnection:Type"] = "MsalAuth",
                    ["Connections:ServiceConnection:Settings:AuthType"] = "UserManagedIdentity",
                    ["Connections:ServiceConnection:Settings:ClientId"] = "00000000-0000-0000-0000-000000000001",
                    ["Connections:ServiceConnection:Settings:Scopes:0"] = "https://api.botframework.com/.default",
                    ["ConnectionsMap:0:ServiceUrl"] = "*",
                    ["ConnectionsMap:0:Connection"] = "ServiceConnection",
                }));
        }

        private void ReplaceMetadata(IServiceCollection services, string scheme, string issuer) =>
            services.PostConfigure<JwtBearerOptions>(scheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
                configuration.SigningKeys.Add(SigningKey);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
    }
}