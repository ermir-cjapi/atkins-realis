using Microsoft.Agents.Builder;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests the manual sign-in fallback sent when Teams silent SSO fails.</summary>
public sealed class TeamsSsoFallbackMiddlewareTests
{
    private const string ConnectionName = "user.read";
    private const string SignInLink = "https://token.botframework.com/api/oauth/signin?signin=abc";

    /// <summary>A signin/failure invoke produces one OAuth card with a sign-in button and no token exchange resource.</summary>
    [Fact]
    public async Task SignInFailureSendsManualSignInCard()
    {
        var adapter = new RecordingAdapter();
        var middleware = CreateMiddleware(new MemoryCache(new MemoryCacheOptions()));

        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);

        var sent = Assert.Single(adapter.Sent);
        Assert.Equal(ActivityTypes.Message, sent.Type);
        var attachment = Assert.Single(sent.Attachments);
        Assert.Equal(OAuthCard.ContentType, attachment.ContentType);
        var card = Assert.IsType<OAuthCard>(attachment.Content);
        Assert.Equal(ConnectionName, card.ConnectionName);
        Assert.Equal(TeamsSsoFallbackMiddleware.FallbackText, card.Text);
        Assert.Null(card.TokenExchangeResource);
        var button = Assert.Single(card.Buttons);
        Assert.Equal(ActionTypes.Signin, button.Type);
        Assert.Equal(SignInLink, button.Value);
    }

    /// <summary>Repeated failures from the same user and conversation within the cooldown send only one card.</summary>
    [Fact]
    public async Task RepeatedFailuresAreRateLimited()
    {
        var adapter = new RecordingAdapter();
        var middleware = CreateMiddleware(new MemoryCache(new MemoryCacheOptions()));

        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);

        Assert.Single(adapter.Sent);
    }

    /// <summary>Other activities and disabled SSO never produce a fallback card.</summary>
    [Theory]
    [InlineData(ActivityTypes.Message, null, true)]
    [InlineData(ActivityTypes.Invoke, SignInConstants.TokenExchangeOperationName, true)]
    [InlineData(ActivityTypes.Invoke, SignInConstants.SignInFailure, false)]
    public async Task OtherTurnsAreIgnored(string type, string? name, bool ssoEnabled)
    {
        var adapter = new RecordingAdapter();
        var middleware = CreateMiddleware(new MemoryCache(new MemoryCacheOptions()), ssoEnabled);
        var activity = SignInFailureActivity();
        activity.Type = type;
        activity.Name = name;
        var nextCalled = false;

        await middleware.OnTurnAsync(
            CreateContext(adapter, activity),
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(nextCalled);
        Assert.Empty(adapter.Sent);
    }

    /// <summary>Token service failures are swallowed so the invoke turn still completes.</summary>
    [Fact]
    public async Task FailuresDoNotThrow()
    {
        var adapter = new RecordingAdapter();
        var middleware = CreateMiddleware(new MemoryCache(new MemoryCacheOptions()));

        await middleware.OnTurnAsync(
            CreateContext(adapter, SignInFailureActivity(), new FakeUserTokenClient(throws: true)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Empty(adapter.Sent);
    }

    private static TeamsSsoFallbackMiddleware CreateMiddleware(IMemoryCache cache, bool ssoEnabled = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentApplication:UserAuthorization:Handlers:hr:Settings:AzureBotOAuthConnectionName"] = ConnectionName,
            })
            .Build();
        return new TeamsSsoFallbackMiddleware(
            MsOptions.Create(new TeamsOptions { SsoEnabled = ssoEnabled, AuthorizationHandler = "hr" }),
            configuration,
            cache,
            NullLogger<TeamsSsoFallbackMiddleware>.Instance);
    }

    private static TurnContext CreateContext(RecordingAdapter adapter, Activity activity, IUserTokenClient? client = null)
    {
        var context = new TurnContext(adapter, activity);
        context.Services.Set(client ?? new FakeUserTokenClient(throws: false));
        return context;
    }

    private static Activity SignInFailureActivity() => new()
    {
        Type = ActivityTypes.Invoke,
        Name = SignInConstants.SignInFailure,
        ChannelId = "msteams",
        Conversation = new ConversationAccount { Id = "conversation-1" },
        From = new ChannelAccount { Id = "user-1" },
        Recipient = new ChannelAccount { Id = "bot-1" },
    };

    private sealed class RecordingAdapter : ChannelAdapter
    {
        public List<IActivity> Sent { get; } = [];

        public override Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext turnContext, IActivity[] activities, CancellationToken cancellationToken)
        {
            Sent.AddRange(activities);
            return Task.FromResult(activities.Select(_ => new ResourceResponse()).ToArray());
        }
    }

    private sealed class FakeUserTokenClient(bool throws) : IUserTokenClient
    {
        public Task<SignInResource> GetSignInResourceAsync(
            string connectionName, IActivity activity, string finalRediect, CancellationToken cancellationToken) =>
            throws
                ? throw new InvalidOperationException("token service unavailable")
                : Task.FromResult(new SignInResource { SignInLink = SignInLink });

        public Task<TokenResponse> GetUserTokenAsync(string userId, string connectionName, ChannelId channelId, string magicCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SignOutUserAsync(string userId, string connectionName, ChannelId channelId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TokenStatus[]> GetTokenStatusAsync(string userId, ChannelId channelId, string includeFilter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Dictionary<string, TokenResponse>> GetAadTokensAsync(string userId, string connectionName, string[] resourceUrls, ChannelId channelId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TokenResponse> ExchangeTokenAsync(string userId, string connectionName, ChannelId channelId, TokenExchangeRequest exchangeRequest, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TokenOrSignInResourceResponse> GetTokenOrSignInResourceAsync(string connectionName, IActivity activity, string code, string finalRedirect, string fwdUrl, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
