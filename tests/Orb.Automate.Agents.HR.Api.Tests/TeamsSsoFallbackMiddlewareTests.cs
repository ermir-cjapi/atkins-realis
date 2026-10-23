using System.Reflection;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Builder.UserAuth.TokenService;
using Microsoft.Agents.Storage;
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
        var middleware = CreateMiddleware(new MemoryStorage());

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
        var middleware = CreateMiddleware(new MemoryStorage());

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
        var middleware = CreateMiddleware(new MemoryStorage(), ssoEnabled);
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
        var middleware = CreateMiddleware(new MemoryStorage());

        await middleware.OnTurnAsync(
            CreateContext(adapter, SignInFailureActivity(), new FakeUserTokenClient(throws: true)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Empty(adapter.Sent);
    }

    /// <summary>
    /// After a fallback card, the verifyState invoke is redeemed by the middleware (not the SDK), answered with
    /// an explicit invoke response and confirmed to the user.
    /// </summary>
    [Theory]
    [InlineData("user-token", false, 200, true)]
    [InlineData(null, false, 404, false)]
    [InlineData("user-token", true, 500, false)]
    public async Task VerifyStateAfterFallbackCardIsCompleted(string? token, bool throws, int expectedStatus, bool expectConfirmation)
    {
        var adapter = new RecordingAdapter();
        var storage = new MemoryStorage();
        var middleware = CreateMiddleware(storage);
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        adapter.Sent.Clear();
        var client = new FakeUserTokenClient(throws, token);
        var nextCalled = false;

        await middleware.OnTurnAsync(
            CreateContext(adapter, VerifyStateActivity("123456"), client),
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(nextCalled);
        var invokeResponse = adapter.Sent[0];
        Assert.Equal(ActivityTypes.InvokeResponse, invokeResponse.Type);
        Assert.Equal(expectedStatus, Assert.IsType<InvokeResponse>(invokeResponse.Value).Status);
        if (expectConfirmation)
        {
            Assert.Equal("123456", client.MagicCode);
            Assert.Equal(2, adapter.Sent.Count);
            Assert.Equal(TeamsSsoFallbackMiddleware.SignedInText, adapter.Sent[1].Text);
        }
        else
        {
            Assert.Single(adapter.Sent);
        }
    }

    /// <summary>A user cancelling the manual sign-in is acknowledged without calling the token service.</summary>
    [Fact]
    public async Task VerifyStateCancelledByUserIsAcknowledged()
    {
        var adapter = new RecordingAdapter();
        var middleware = CreateMiddleware(new MemoryStorage());
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        adapter.Sent.Clear();
        var client = new FakeUserTokenClient(throws: false);

        await middleware.OnTurnAsync(
            CreateContext(adapter, VerifyStateActivity("CancelledByUser"), client),
            _ => Task.CompletedTask,
            CancellationToken.None);

        var sent = Assert.Single(adapter.Sent);
        Assert.Equal(200, Assert.IsType<InvokeResponse>(sent.Value).Status);
        Assert.Null(client.MagicCode);
    }

    /// <summary>Without an outstanding fallback card, verifyState is left to the SDK.</summary>
    [Fact]
    public async Task VerifyStateWithoutFallbackCardIsPassedThrough()
    {
        var adapter = new RecordingAdapter();
        var middleware = CreateMiddleware(new MemoryStorage());
        var nextCalled = false;

        await middleware.OnTurnAsync(
            CreateContext(adapter, VerifyStateActivity("123456")),
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(nextCalled);
        Assert.Empty(adapter.Sent);
    }

    private static Activity VerifyStateActivity(string state)
    {
        var activity = SignInFailureActivity();
        activity.Name = SignInConstants.VerifyStateOperationName;
        activity.Value = new Dictionary<string, object> { ["state"] = state };
        return activity;
    }

    /// <summary>A second app instance and a card older than the cooldown can still complete sign-in.</summary>
    [Fact]
    public async Task VerificationSurvivesRestartAndCooldownExpiry()
    {
        var storage = new MemoryStorage();
        var time = new TestTimeProvider();
        var adapter = new RecordingAdapter();
        await CreateMiddleware(storage, timeProvider: time).OnTurnAsync(
            CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        time.Now += TeamsSsoFallbackMiddleware.CooldownPeriod + TimeSpan.FromMinutes(1);
        adapter.Sent.Clear();

        await CreateMiddleware(storage, timeProvider: time).OnTurnAsync(
            CreateContext(adapter, VerifyStateActivity("123456")),
            _ => throw new InvalidOperationException("Verification must not start a new SDK flow"), CancellationToken.None);

        Assert.Equal(200, Assert.IsType<InvokeResponse>(adapter.Sent[0].Value).Status);
        Assert.Equal(TeamsSsoFallbackMiddleware.SignedInText, adapter.Sent[1].Text);
    }

    /// <summary>Retried invokes use the existing token and do not repeat the confirmation.</summary>
    [Fact]
    public async Task VerificationRetryIsAcknowledgedWithoutDuplicateConfirmation()
    {
        var storage = new MemoryStorage();
        var middleware = CreateMiddleware(storage);
        var adapter = new RecordingAdapter();
        var client = new FakeUserTokenClient(false);
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity(), client), _ => Task.CompletedTask, CancellationToken.None);
        await middleware.OnTurnAsync(CreateContext(adapter, VerifyStateActivity("123456"), client), _ => Task.CompletedTask, CancellationToken.None);
        adapter.Sent.Clear();

        await CreateMiddleware(storage).OnTurnAsync(CreateContext(adapter, VerifyStateActivity("123456"), client),
            _ => throw new InvalidOperationException("Retry must not reach SDK"), CancellationToken.None);

        Assert.Equal(200, Assert.IsType<InvokeResponse>(Assert.Single(adapter.Sent).Value).Status);
    }

    /// <summary>Signing out immediately releases the fallback cooldown.</summary>
    [Fact]
    public async Task SignOutClearsFallbackState()
    {
        var storage = new MemoryStorage();
        var middleware = CreateMiddleware(storage);
        var adapter = new RecordingAdapter();
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        var signout = SignInFailureActivity();
        signout.Type = ActivityTypes.Message;
        signout.Text = " /SIGNOUT ";
        var nextCalled = false;
        await middleware.OnTurnAsync(CreateContext(adapter, signout), _ => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None);
        adapter.Sent.Clear();

        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(nextCalled);
        Assert.Single(adapter.Sent);
    }

    /// <summary>Completion does not suppress the card for a new authentication failure.</summary>
    [Fact]
    public async Task NewFailureAfterSuccessfulVerificationSendsNewCard()
    {
        var middleware = CreateMiddleware(new MemoryStorage());
        var adapter = new RecordingAdapter();
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        await middleware.OnTurnAsync(CreateContext(adapter, VerifyStateActivity("123456")), _ => Task.CompletedTask, CancellationToken.None);
        adapter.Sent.Clear();

        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);

        Assert.Single(Assert.Single(adapter.Sent).Attachments);
    }

    /// <summary>Fallback ownership is scoped to user, conversation and channel, and respects the feature switch.</summary>
    [Theory]
    [InlineData("other-user", "conversation-1", "msteams", true)]
    [InlineData("user-1", "other-conversation", "msteams", true)]
    [InlineData("user-1", "conversation-1", "other-channel", true)]
    [InlineData("user-1", "conversation-1", "msteams", false)]
    public async Task VerificationDoesNotInterceptUnrelatedFlows(string user, string conversation, string channel, bool enabled)
    {
        var storage = new MemoryStorage();
        var adapter = new RecordingAdapter();
        await CreateMiddleware(storage).OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        adapter.Sent.Clear();
        var activity = VerifyStateActivity("123456");
        activity.From.Id = user;
        activity.Conversation.Id = conversation;
        activity.ChannelId = channel;
        var nextCalled = false;

        await CreateMiddleware(storage, enabled).OnTurnAsync(CreateContext(adapter, activity),
            _ => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(nextCalled);
        Assert.Empty(adapter.Sent);
    }

    /// <summary>Malformed callbacks are rejected without attempting token redemption.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(123)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidStateReturnsBadRequest(object? state)
    {
        var middleware = CreateMiddleware(new MemoryStorage());
        var adapter = new RecordingAdapter();
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        adapter.Sent.Clear();
        var activity = VerifyStateActivity("unused");
        activity.Value = new Dictionary<string, object?> { ["state"] = state };

        await middleware.OnTurnAsync(CreateContext(adapter, activity, new FakeUserTokenClient(true)), _ => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<InvokeResponse>(Assert.Single(adapter.Sent).Value).Status);
    }

    /// <summary>Request cancellation must propagate rather than being converted into an authentication error.</summary>
    [Fact]
    public async Task VerificationCancellationPropagates()
    {
        var middleware = CreateMiddleware(new MemoryStorage());
        var adapter = new RecordingAdapter();
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        adapter.Sent.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.OnTurnAsync(
            CreateContext(adapter, VerifyStateActivity("123456")), _ => Task.CompletedTask, cancellation.Token));

        Assert.Empty(adapter.Sent);
    }

    /// <summary>Real SDK OAuth flow state cannot leave the user stuck after fallback redemption.</summary>
    [Fact]
    public async Task NextMessageUsesRedeemedTokenEvenIfSdkFlowWasRestarted()
    {
        var storage = new MemoryStorage();
        var middleware = CreateMiddleware(storage);
        var adapter = new RecordingAdapter();
        var client = new FakeUserTokenClient(false);
        var handler = new AzureBotUserAuthorization("hr", storage,
            DispatchProxy.Create<IConnections, UnusedConnections>(),
            new OAuthSettings { AzureBotOAuthConnectionName = ConnectionName, EnableSso = true });
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity(), client), _ => Task.CompletedTask, CancellationToken.None);

        // The user sends another message while the fallback card is outstanding: SDK starts a new flow.
        var message = SignInFailureActivity();
        message.Type = ActivityTypes.Message;
        message.Name = null;
        message.Text = "hello";
        Assert.Null(await handler.SignInUserAsync(CreateContext(adapter, message, client)));
        adapter.Sent.Clear();
        await middleware.OnTurnAsync(CreateContext(adapter, VerifyStateActivity("123456"), client), _ => Task.CompletedTask, CancellationToken.None);
        adapter.Sent.Clear();

        var token = await handler.SignInUserAsync(CreateContext(adapter, message, client));

        Assert.Equal("user-token", token.Token);
        Assert.Empty(adapter.Sent);
    }

    /// <summary>The SDK failure/reset followed by fallback redemption leaves a usable token for the next message.</summary>
    [Fact]
    public async Task RealSdkFailureThenFallbackCompletesAndAuthenticatesNextMessage()
    {
        var storage = new MemoryStorage();
        var middleware = CreateMiddleware(storage);
        var adapter = new RecordingAdapter();
        var client = new FakeUserTokenClient(false);
        var handler = new AzureBotUserAuthorization("hr", storage,
            DispatchProxy.Create<IConnections, UnusedConnections>(),
            new OAuthSettings { AzureBotOAuthConnectionName = ConnectionName, EnableSso = true });
        var message = SignInFailureActivity();
        message.Type = ActivityTypes.Message;
        message.Name = null;
        message.Text = "hello";
        Assert.Null(await handler.SignInUserAsync(CreateContext(adapter, message, client)));
        var failure = SignInFailureActivity();
        failure.Value = new { code = "resourcematchfailed", message = "Resource match failed" };
        var failureContext = CreateContext(adapter, failure, client);

        await middleware.OnTurnAsync(failureContext, async _ =>
        {
            // AgentApplication normally catches this and resets the dispatcher state.
            await Assert.ThrowsAnyAsync<Exception>(() => handler.SignInUserAsync(failureContext));
        }, CancellationToken.None);
        adapter.Sent.Clear();
        await middleware.OnTurnAsync(CreateContext(adapter, VerifyStateActivity("123456"), client),
            _ => throw new InvalidOperationException("Must not restart SDK flow"), CancellationToken.None);

        Assert.Equal(200, Assert.IsType<InvokeResponse>(adapter.Sent[0].Value).Status);
        adapter.Sent.Clear();
        var token = await handler.SignInUserAsync(CreateContext(adapter, message, client));
        Assert.Equal("user-token", token.Token);
        Assert.Empty(adapter.Sent);
    }

    /// <summary>A temporary missing token leaves the existing card usable for another verification.</summary>
    [Fact]
    public async Task MissingTokenCanBeRetriedSuccessfully()
    {
        var storage = new MemoryStorage();
        var middleware = CreateMiddleware(storage);
        var adapter = new RecordingAdapter();
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        adapter.Sent.Clear();
        await middleware.OnTurnAsync(CreateContext(adapter, VerifyStateActivity("123456"), new FakeUserTokenClient(false, null)),
            _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(404, Assert.IsType<InvokeResponse>(Assert.Single(adapter.Sent).Value).Status);
        adapter.Sent.Clear();

        await middleware.OnTurnAsync(CreateContext(adapter, VerifyStateActivity("123456")), _ => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(200, Assert.IsType<InvokeResponse>(adapter.Sent[0].Value).Status);
        Assert.Equal(TeamsSsoFallbackMiddleware.SignedInText, adapter.Sent[1].Text);
    }

    /// <summary>Completing a group fallback cannot delete a different group member's shared SDK flow.</summary>
    [Fact]
    public async Task GroupVerificationPreservesConversationWideSdkFlow()
    {
        var storage = new MemoryStorage();
        const string flowKey = "oauth/hr/msteams/conversation-1/flowState";
        await storage.WriteAsync(new Dictionary<string, object> { [flowKey] = new Dictionary<string, object> { ["otherUser"] = true } }, CancellationToken.None);
        var middleware = CreateMiddleware(storage);
        var adapter = new RecordingAdapter();
        var failure = SignInFailureActivity();
        failure.Conversation.ConversationType = "groupChat";
        await middleware.OnTurnAsync(CreateContext(adapter, failure), _ => Task.CompletedTask, CancellationToken.None);
        var verify = VerifyStateActivity("123456");
        verify.Conversation.ConversationType = "groupChat";

        await middleware.OnTurnAsync(CreateContext(adapter, verify), _ => Task.CompletedTask, CancellationToken.None);

        Assert.True((await storage.ReadAsync([flowKey], CancellationToken.None)).ContainsKey(flowKey));
    }

    /// <summary>A transient card delivery failure must not hide the next fallback card for fifteen minutes.</summary>
    [Fact]
    public async Task FailedCardDeliveryDoesNotStartCooldown()
    {
        var middleware = CreateMiddleware(new MemoryStorage());
        var adapter = new RecordingAdapter { FailSends = true };
        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);
        adapter.FailSends = false;

        await middleware.OnTurnAsync(CreateContext(adapter, SignInFailureActivity()), _ => Task.CompletedTask, CancellationToken.None);

        Assert.Single(adapter.Sent);
    }

    /// <summary>Connections must not be used when no OBO scopes are configured in this test.</summary>
    public class UnusedConnections : DispatchProxy
    {
        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TeamsSsoFallbackMiddleware CreateMiddleware(IStorage storage, bool ssoEnabled = true, TimeProvider? timeProvider = null)
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
            storage,
            timeProvider ?? TimeProvider.System,
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
        Conversation = new ConversationAccount { Id = "conversation-1", ConversationType = "personal" },
        From = new ChannelAccount { Id = "user-1" },
        Recipient = new ChannelAccount { Id = "bot-1" },
    };

    private sealed class RecordingAdapter : ChannelAdapter
    {
        public List<IActivity> Sent { get; } = [];

        public bool FailSends { get; set; }

        public override Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext turnContext, IActivity[] activities, CancellationToken cancellationToken)
        {
            if (FailSends)
            {
                throw new InvalidOperationException("Channel temporarily unavailable");
            }
            Sent.AddRange(activities);
            return Task.FromResult(activities.Select(_ => new ResourceResponse()).ToArray());
        }
    }

    private sealed class FakeUserTokenClient(bool throws, string? token = "user-token") : IUserTokenClient
    {
        public string? MagicCode { get; private set; }

        public bool Redeemed { get; set; }

        public Task<SignInResource> GetSignInResourceAsync(
            string connectionName, IActivity activity, string finalRediect, CancellationToken cancellationToken) =>
            throws
                ? throw new InvalidOperationException("token service unavailable")
                : Task.FromResult(new SignInResource { SignInLink = SignInLink });

        public Task<TokenResponse> GetUserTokenAsync(string userId, string connectionName, ChannelId channelId, string magicCode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (throws)
            {
                throw new InvalidOperationException("token service unavailable");
            }

            if (!string.IsNullOrEmpty(magicCode))
            {
                MagicCode = magicCode;
                Redeemed = token is not null;
            }
            return Task.FromResult(!Redeemed || token is null ? null! : new TokenResponse { Token = token, ConnectionName = connectionName });
        }

        public Task SignOutUserAsync(string userId, string connectionName, ChannelId channelId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TokenStatus[]> GetTokenStatusAsync(string userId, ChannelId channelId, string includeFilter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Dictionary<string, TokenResponse>> GetAadTokensAsync(string userId, string connectionName, string[] resourceUrls, ChannelId channelId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TokenResponse> ExchangeTokenAsync(string userId, string connectionName, ChannelId channelId, TokenExchangeRequest exchangeRequest, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TokenOrSignInResourceResponse> GetTokenOrSignInResourceAsync(string connectionName, IActivity activity, string code, string finalRedirect, string fwdUrl, CancellationToken cancellationToken) =>
            Task.FromResult(new TokenOrSignInResourceResponse
            {
                TokenResponse = Redeemed && token is not null ? new TokenResponse { Token = token, ConnectionName = connectionName } : null!,
                SignInResource = new SignInResource { SignInLink = SignInLink },
            });

        public void Dispose()
        {
        }
    }
}
