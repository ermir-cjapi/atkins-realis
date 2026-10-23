using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Orb.Automate.Agents.HR.Api.Agent;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests the Teams <c>/signout</c> command.</summary>
public sealed class TeamsSignOutTests : IClassFixture<TeamsEndpointTests.TeamsApplication>
{
    private const string BotAppId = "00000000-0000-0000-0000-000000000001";
    private const string EntraTenantId = "00000000-0000-0000-0000-000000000000";
    private const string BotFrameworkIssuer = "https://api.botframework.com";
    private const string ServiceUrl = "https://smba.trafficmanager.net/emea/";
    private const string UserId = "signout-user-id";

    private readonly TeamsEndpointTests.TeamsApplication _application;

    /// <summary>Creates the test class over the shared Teams-enabled host.</summary>
    public TeamsSignOutTests(TeamsEndpointTests.TeamsApplication application) => _application = application;

    /// <summary>The command is matched trimmed and case-insensitively and never reaches the chat service.</summary>
    [Theory]
    [InlineData("/signout")]
    [InlineData(" /SignOut ")]
    public async Task SignOutRepliesWithoutCallingChatService(string text)
    {
        var conversationId = $"personal-{Guid.NewGuid():N}";
        using var client = CreateClient(_application);

        var activities = await SendMessageAsync(client, _application.SigningKey, conversationId, "personal", text);

        Assert.Equal([HrTeamsAgent.SignOutReply], Texts(activities));
        Assert.DoesNotContain(_application.ChatService.Turns, turn => turn.ConversationId == conversationId);
    }

    /// <summary>A personal chat starts from scratch after sign-out, and the per-user keys are removed.</summary>
    [Fact]
    public async Task PersonalSignOutClearsHistoryAndUserState()
    {
        var conversationId = $"personal-{Guid.NewGuid():N}";
        var storage = _application.Services.GetRequiredService<IStorage>();
        using var client = CreateClient(_application);

        var key = _application.SigningKey;
        await SendMessageAsync(client, key, conversationId, "personal", "Can I carry over leave?");
        var keys = await SeedUserKeysAsync(storage, conversationId);
        await SendMessageAsync(client, key, conversationId, "personal", "/signout");
        var remaining = await storage.ReadAsync([ConversationKey(conversationId), .. keys], CancellationToken.None);
        await SendMessageAsync(client, key, conversationId, "personal", "new topic");

        Assert.Empty(remaining);
        var turns = _application.ChatService.Turns.Where(turn => turn.ConversationId == conversationId).ToList();
        Assert.Equal(2, turns.Count);
        Assert.Empty(turns[1].History!);
        Assert.True(turns[1].IsConversationStart);
    }

    /// <summary>A group chat keeps shared conversation state and only clears the sender's state.</summary>
    [Fact]
    public async Task GroupSignOutKeepsConversationState()
    {
        var conversationId = $"group-{Guid.NewGuid():N}";
        var storage = _application.Services.GetRequiredService<IStorage>();
        using var client = CreateClient(_application);

        await SendMessageAsync(client, _application.SigningKey, conversationId, "groupChat", "Can I carry over leave?");
        var keys = await SeedUserKeysAsync(storage, conversationId);
        var activities = await SendMessageAsync(client, _application.SigningKey, conversationId, "groupChat", "/signout");

        Assert.Equal([HrTeamsAgent.GroupSignOutReply], Texts(activities));
        Assert.Contains(ConversationKey(conversationId), await storage.ReadAsync([ConversationKey(conversationId)], CancellationToken.None));
        Assert.Empty(await storage.ReadAsync(keys.Where(key => !key.EndsWith("/flowState", StringComparison.Ordinal)).ToArray(), CancellationToken.None));
        Assert.Contains($"oauth/hr/msteams/{conversationId}/flowState",
            await storage.ReadAsync([$"oauth/hr/msteams/{conversationId}/flowState"], CancellationToken.None));
    }

    /// <summary>With SSO enabled and no token, the command signs out without prompting for sign-in.</summary>
    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task SsoSignOutCallsTokenServiceWithoutSignInCard(string autoSignIn)
    {
        var tokenClient = new FakeUserTokenClient();
        using var host = new TeamsEndpointTests.TeamsApplication();
        using var application = CreateSsoApplication(host, tokenClient, autoSignIn);
        var storage = application.Services.GetRequiredService<IStorage>();
        using var client = CreateClient(application);
        var conversationId = $"personal-{Guid.NewGuid():N}";
        var keys = await SeedUserKeysAsync(storage, conversationId);

        var activities = await SendMessageAsync(client, host.SigningKey, conversationId, "personal", "/signout");

        Assert.Equal([HrTeamsAgent.SignOutReply], Texts(activities));
        Assert.All(activities, activity => Assert.False(
            activity.TryGetProperty("attachments", out var attachments) && attachments.GetArrayLength() > 0));
        var call = Assert.Single(tokenClient.SignOutCalls);
        Assert.Equal((UserId, "user.read"), call);
        Assert.Equal(0, tokenClient.SignInRequests);
        Assert.Empty(await storage.ReadAsync([.. keys], CancellationToken.None));
        Assert.Empty(host.ChatService.Turns);
    }

    /// <summary>A Token Service failure still clears state and tells the user sign-out may be incomplete.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTokenServiceSignOutStillClearsState(bool timeout)
    {
        var tokenClient = new FakeUserTokenClient
        {
            SignOutException = timeout
                ? new TaskCanceledException("Token Service timed out")
                : new HttpRequestException("Token Service unavailable"),
        };
        using var host = new TeamsEndpointTests.TeamsApplication();
        using var application = CreateSsoApplication(host, tokenClient, "false");
        var storage = application.Services.GetRequiredService<IStorage>();
        using var client = CreateClient(application);
        var conversationId = $"personal-{Guid.NewGuid():N}";
        var keys = await SeedUserKeysAsync(storage, conversationId);
        await storage.WriteAsync(new Dictionary<string, object>
        {
            [ConversationKey(conversationId)] = new Dictionary<string, object>
            {
                ["hrHistory"] = "[{\"userMessage\":\"question\",\"assistantMessage\":\"answer\"}]",
                ["hrUserMessageCount"] = 7,
            },
        }, CancellationToken.None);

        var activities = await SendMessageAsync(client, host.SigningKey, conversationId, "personal", "/signout");

        Assert.Equal([HrTeamsAgent.SignOutIncompleteReply], Texts(activities));
        Assert.Single(tokenClient.SignOutCalls);
        Assert.Empty(await storage.ReadAsync([ConversationKey(conversationId), .. keys], CancellationToken.None));
    }

    /// <summary>Signing out in a shared chat must leave another member's pending sign-in and shared flow untouched.</summary>
    [Theory]
    [InlineData("groupChat")]
    [InlineData("channel")]
    public async Task SharedChatSignOutPreservesOtherMembersPendingFlow(string conversationType)
    {
        var tokenClient = new FakeUserTokenClient();
        using var host = new TeamsEndpointTests.TeamsApplication();
        using var application = CreateSsoApplication(host, tokenClient, "true");
        using var client = CreateClient(application);
        var storage = application.Services.GetRequiredService<IStorage>();
        var conversationId = $"shared-{Guid.NewGuid():N}";
        var keys = await SeedUserKeysAsync(storage, conversationId);
        var sharedFlowKey = $"oauth/hr/msteams/{conversationId}/flowState";
        const string otherUser = "other-member";
        string[] preservedKeys =
        [
            sharedFlowKey,
            ConversationKey(conversationId),
            $"oauth/msteams/{otherUser}/userAuthorizationState",
            $"msteams/users/{otherUser}",
            $"msteams/conversations/{conversationId}/users/{otherUser}",
        ];
        await storage.WriteAsync(new Dictionary<string, object>
        {
            [ConversationKey(conversationId)] = new Dictionary<string, object> { ["shared"] = "keep" },
            [preservedKeys[2]] = PendingSignInState(),
            [preservedKeys[3]] = new Dictionary<string, object> { ["user"] = "keep" },
            [preservedKeys[4]] = new Dictionary<string, object> { ["private"] = "keep" },
        }, CancellationToken.None);
        var before = await storage.ReadAsync(preservedKeys, CancellationToken.None);

        var activities = await SendMessageAsync(client, host.SigningKey, conversationId, conversationType, " /SignOut ");

        Assert.Equal([HrTeamsAgent.GroupSignOutReply], Texts(activities));
        Assert.Equal((UserId, "user.read"), Assert.Single(tokenClient.SignOutCalls));
        Assert.Equal(0, tokenClient.SignInRequests);
        Assert.Empty(host.ChatService.Turns);
        Assert.Empty(await storage.ReadAsync(keys.Where(key => key != sharedFlowKey).ToArray(), CancellationToken.None));
        var after = await storage.ReadAsync(preservedKeys, CancellationToken.None);
        Assert.Equal(preservedKeys.Length, after.Count);
        foreach (var key in preservedKeys)
        {
            Assert.Equal(JsonSerializer.Serialize(before[key]), JsonSerializer.Serialize(after[key]));
        }
    }

    private static WebApplicationFactory<Program> CreateSsoApplication(
        TeamsEndpointTests.TeamsApplication host,
        FakeUserTokenClient tokenClient,
        string autoSignIn) =>
        host.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Teams:SsoEnabled", "true");
            builder.UseSetting("AgentApplication:UserAuthorization:AutoSignIn", autoSignIn);
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IChannelServiceClientFactory>(provider => new FakeChannelServiceClientFactory(
                    ActivatorUtilities.CreateInstance<RestChannelServiceClientFactory>(provider),
                    tokenClient)));
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> application) =>
        application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string ConversationKey(string conversationId) => $"msteams/conversations/{conversationId}";

    // The SDK checks the actual internal SignInState type, not a dictionary with similar properties.
    // Reflection is limited to test setup so this exercises a real pending flow before adapter dispatch.
    private static object PendingSignInState()
    {
        var type = typeof(Microsoft.Agents.Builder.App.AgentApplication).Assembly.GetType(
            "Microsoft.Agents.Builder.App.UserAuth.SignInState", throwOnError: true)!;
        var state = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetProperty("ActiveHandler")!.SetValue(state, "hr");
        return state;
    }

    // Seeds sender state and the SDK's conversation-wide OAuth flow record.
    private static async Task<string[]> SeedUserKeysAsync(IStorage storage, string conversationId)
    {
        string[] keys =
        [
            $"oauth/msteams/{UserId}/userAuthorizationState",
            $"oauth/hr/msteams/{conversationId}/flowState",
            $"msteams/users/{UserId}",
            $"msteams/conversations/{conversationId}/users/{UserId}",
        ];
        var values = keys.ToDictionary(key => key, _ => (object)new Dictionary<string, object> { ["seeded"] = true });
        values[keys[0]] = PendingSignInState();
        await storage.WriteAsync(values, CancellationToken.None);
        return keys;
    }

    private static string[] Texts(IEnumerable<JsonElement> activities) =>
        [.. activities.Select(activity => activity.GetProperty("text").GetString() ?? string.Empty)];

    private static async Task<List<JsonElement>> SendMessageAsync(
        HttpClient client,
        SecurityKey signingKey,
        string conversationId,
        string conversationType,
        string text)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/messages")
        {
            Content = JsonContent.Create(new
            {
                type = "message",
                id = Guid.NewGuid().ToString("N"),
                timestamp = DateTimeOffset.UtcNow,
                serviceUrl = ServiceUrl,
                channelId = "msteams",
                deliveryMode = "expectReplies",
                from = new { id = UserId },
                conversation = new { id = conversationId, tenantId = EntraTenantId, conversationType },
                recipient = new { id = $"28:{BotAppId}" },
                text,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(signingKey));
        var response = await client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"Unexpected status code {(int)response.StatusCode}.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. json.RootElement.GetProperty("activities").EnumerateArray()
            .Where(activity => activity.GetProperty("type").GetString() == "message")
            .Select(activity => activity.Clone())];
    }

    private static string CreateToken(SecurityKey signingKey)
    {
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Issuer = BotFrameworkIssuer,
            Audience = BotAppId,
            Subject = new ClaimsIdentity([new Claim("serviceurl", ServiceUrl)]),
            NotBefore = now.AddMinutes(-1),
            IssuedAt = now.AddMinutes(-1),
            Expires = now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>Delegates connector clients to the real factory and replaces the Token Service client.</summary>
    private sealed class FakeChannelServiceClientFactory(
        IChannelServiceClientFactory inner,
        IUserTokenClient userTokenClient) : IChannelServiceClientFactory
    {
#pragma warning disable CS0618 // The obsolete overload is part of the interface.
        public Task<IConnectorClient> CreateConnectorClientAsync(
            ClaimsIdentity claimsIdentity,
            string serviceUrl,
            string audience,
            CancellationToken cancellationToken,
            IList<string>? scopes = null,
            bool useAnonymous = false) =>
            inner.CreateConnectorClientAsync(claimsIdentity, serviceUrl, audience, cancellationToken, scopes!, useAnonymous);
#pragma warning restore CS0618

        public Task<IConnectorClient> CreateConnectorClientAsync(
            ITurnContext turnContext,
            string? audience = null,
            IList<string>? scopes = null,
            bool useAnonymous = false,
            CancellationToken cancellationToken = default) =>
            inner.CreateConnectorClientAsync(turnContext, audience!, scopes!, useAnonymous, cancellationToken);

        public Task<IUserTokenClient> CreateUserTokenClientAsync(
            ClaimsIdentity claimsIdentity,
            bool? useAnonymous = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(userTokenClient);
    }

    /// <summary>Records sign-out calls; any attempt to fetch a token or sign-in resource is counted.</summary>
    private sealed class FakeUserTokenClient : IUserTokenClient
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string UserId, string ConnectionName)> SignOutCalls { get; } = new();

        public Exception? SignOutException { get; init; }

        public int SignInRequests;

        public Task SignOutUserAsync(string userId, string connectionName, ChannelId channelId, CancellationToken cancellationToken)
        {
            SignOutCalls.Enqueue((userId, connectionName));
            return SignOutException is null ? Task.CompletedTask : Task.FromException(SignOutException);
        }

        public Task<TokenResponse> GetUserTokenAsync(string userId, string connectionName, ChannelId channelId, string magicCode, CancellationToken cancellationToken) =>
            Requested<TokenResponse>();

        public Task<SignInResource> GetSignInResourceAsync(string connectionName, IActivity activity, string finalRedirect, CancellationToken cancellationToken) =>
            Requested<SignInResource>();

        public Task<TokenStatus[]> GetTokenStatusAsync(string userId, ChannelId channelId, string includeFilter, CancellationToken cancellationToken) =>
            Requested<TokenStatus[]>();

        public Task<Dictionary<string, TokenResponse>> GetAadTokensAsync(string userId, string connectionName, string[] resourceUrls, ChannelId channelId, CancellationToken cancellationToken) =>
            Requested<Dictionary<string, TokenResponse>>();

        public Task<TokenResponse> ExchangeTokenAsync(string userId, string connectionName, ChannelId channelId, TokenExchangeRequest exchangeRequest, CancellationToken cancellationToken) =>
            Requested<TokenResponse>();

        public Task<TokenOrSignInResourceResponse> GetTokenOrSignInResourceAsync(string connectionName, IActivity activity, string code, string? finalRedirect = null, string? fwdUrl = null, CancellationToken cancellationToken = default) =>
            Requested<TokenOrSignInResourceResponse>();

        public void Dispose()
        {
        }

        private Task<T> Requested<T>()
        {
            Interlocked.Increment(ref SignInRequests);
            return Task.FromException<T>(new InvalidOperationException("The sign-out route must not request a token."));
        }
    }
}
