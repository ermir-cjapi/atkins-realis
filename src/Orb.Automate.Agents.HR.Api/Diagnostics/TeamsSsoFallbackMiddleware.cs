using System.Net;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.UserAuth.TokenService;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// When the Teams client reports that silent SSO failed (<c>signin/failure</c>), the Agents SDK ends the
/// turn without replying. This adapter middleware then sends a manual sign-in OAuth card (no token
/// exchange resource) so the user gets a sign-in button instead of silence. At most one card is sent per
/// user and conversation within <see cref="CooldownPeriod"/>. Never logs the sign-in link or token.
/// <para>
/// Because the SDK discards its sign-in flow on <c>signin/failure</c>, it cannot complete the
/// <c>signin/verifyState</c> invoke that follows the manual sign-in (it starts a new flow and the adapter
/// answers 501, which Teams shows as "Unable to reach app"). While a fallback card is outstanding for the
/// user and conversation, this middleware redeems that invoke itself and confirms the sign-in.
/// </para>
/// </summary>
public sealed class TeamsSsoFallbackMiddleware : Microsoft.Agents.Builder.IMiddleware
{
    /// <summary>The text shown on the fallback sign-in card.</summary>
    internal const string FallbackText =
        "Silent sign-in didn't work. Please sign in with the button below, then send your question again.";

    /// <summary>The minimum interval between fallback cards for the same user and conversation.</summary>
    internal static readonly TimeSpan CooldownPeriod = TimeSpan.FromMinutes(15);

    /// <summary>The message sent after a manual sign-in from the fallback card succeeds.</summary>
    internal const string SignedInText = "You're signed in. Please send your question again.";

    private const string SignInButtonTitle = "Sign in";
    private const string CancelledByUser = "CancelledByUser";

    private readonly TeamsOptions _teams;
    private readonly string? _connectionName;
    private readonly IStorage _storage;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TeamsSsoFallbackMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    public TeamsSsoFallbackMiddleware(
        IOptions<TeamsOptions> teamsOptions,
        IConfiguration configuration,
        IStorage storage,
        TimeProvider timeProvider,
        ILogger<TeamsSsoFallbackMiddleware> logger)
    {
        _teams = teamsOptions.Value;
        _connectionName = configuration[
            $"AgentApplication:UserAuthorization:Handlers:{_teams.AuthorizationHandler}:Settings:AzureBotOAuthConnectionName"];
        _storage = storage;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnTurnAsync(ITurnContext turnContext, NextDelegate next, CancellationToken cancellationToken)
    {
        var activity = turnContext.Activity;
        var isInvoke = _teams.SsoEnabled &&
            activity.ChannelId.IsParentChannel("msteams") &&
            string.Equals(activity.Type, ActivityTypes.Invoke, StringComparison.OrdinalIgnoreCase);
        var storageKey = GetStorageKey(activity);

        // Clear only this user's fallback record; the agent owns Token Service sign-out.
        if (_teams.SsoEnabled && Agent.HrTeamsAgent.IsSignOutCommand(activity))
        {
            try
            {
                await _storage.DeleteAsync([storageKey], cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Failed to clear fallback state on sign-out. ErrorType={ErrorType}", exception.GetType().Name);
            }
        }

        if (isInvoke &&
            !string.IsNullOrWhiteSpace(_connectionName) &&
            string.Equals(activity.Name, SignInConstants.VerifyStateOperationName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var fallback = await ReadFallbackAsync(storageKey, cancellationToken);
                if (fallback is not null)
                {
                    await CompleteManualSignInAsync(turnContext, storageKey, fallback, cancellationToken);
                    return;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("Failed to read manual sign-in state. ErrorType={ErrorType}", exception.GetType().Name);
                await SendInvokeResponseAsync(turnContext, HttpStatusCode.InternalServerError, cancellationToken);
                return;
            }
        }

        await next(cancellationToken);

        if (!isInvoke ||
            !string.Equals(activity.Name, SignInConstants.SignInFailure, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(_connectionName))
            {
                _logger.LogWarning(
                    "Cannot send manual sign-in fallback card: handler {AuthorizationHandler} has no AzureBotOAuthConnectionName",
                    _teams.AuthorizationHandler);
                return;
            }

            var fallback = await ReadFallbackAsync(storageKey, cancellationToken);
            if (fallback is { Completed: false } &&
                _timeProvider.GetUtcNow() - fallback.CardSentAt < CooldownPeriod)
            {
                _logger.LogInformation("Manual sign-in fallback card already sent recently; skipping");
                return;
            }

            var signInResource = await UserTokenClientWrapper.GetSignInResourceAsync(
                turnContext, _connectionName, cancellationToken);
            if (string.IsNullOrWhiteSpace(signInResource?.SignInLink))
            {
                _logger.LogWarning("Cannot send manual sign-in fallback card: the token service returned no sign-in link");
                return;
            }

            var card = new OAuthCard
            {
                Text = FallbackText,
                ConnectionName = _connectionName,
                TokenPostResource = signInResource.TokenPostResource,
                Buttons =
                [
                    new CardAction
                    {
                        Type = ActionTypes.Signin,
                        Title = SignInButtonTitle,
                        Value = signInResource.SignInLink,
                    },
                ],
            };

            _logger.LogInformation(
                "Sending manual sign-in fallback card. ConnectionName={ConnectionName}", _connectionName);
            // Persist before sending: another instance can receive the verification immediately.
            await WriteFallbackAsync(storageKey, new FallbackState { CardSentAt = _timeProvider.GetUtcNow() }, cancellationToken);
            try
            {
                await turnContext.SendActivityAsync(
                    new Activity
                    {
                        Type = ActivityTypes.Message,
                        Attachments = [new Attachment { ContentType = OAuthCard.ContentType, Content = card }],
                    },
                    cancellationToken);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                await _storage.DeleteAsync([storageKey], cancellationToken);
                throw;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Failed to send manual sign-in fallback card. ErrorType={ErrorType}", exception.GetType().Name);
        }
    }

    private async Task CompleteManualSignInAsync(
        ITurnContext turnContext, string storageKey, FallbackState fallback, CancellationToken cancellationToken)
    {
        HttpStatusCode status;
        var signedIn = false;
        try
        {
            if (!TryGetState(turnContext.Activity.Value, out var state))
            {
                await SendInvokeResponseAsync(turnContext, HttpStatusCode.BadRequest, cancellationToken);
                return;
            }

            if (string.Equals(state, CancelledByUser, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Manual sign-in from the fallback card was cancelled by the user");
                await ResetSdkStateAsync(turnContext, cancellationToken);
                fallback.Completed = true;
                await WriteFallbackAsync(storageKey, fallback, cancellationToken);
                status = HttpStatusCode.OK;
            }
            else
            {
                // The Token Service may already hold a token (including on a retried invoke).
                var token = await UserTokenClientWrapper.GetUserTokenAsync(
                    turnContext, _connectionName!, null!, cancellationToken);
                if (string.IsNullOrEmpty(token?.Token))
                {
                    token = await UserTokenClientWrapper.GetUserTokenAsync(
                        turnContext, _connectionName!, state!, cancellationToken);
                }
                signedIn = !string.IsNullOrEmpty(token?.Token);
                status = signedIn ? HttpStatusCode.OK : HttpStatusCode.NotFound;
                if (signedIn)
                {
                    await ResetSdkStateAsync(turnContext, cancellationToken);
                }
                _logger.LogInformation(
                    "Manual sign-in from the fallback card completed. SignedIn={SignedIn}", signedIn);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Failed to complete manual sign-in from the fallback card. ErrorType={ErrorType}", exception.GetType().Name);
            signedIn = false;
            status = HttpStatusCode.InternalServerError;
        }

        try
        {
            await SendInvokeResponseAsync(turnContext, status, cancellationToken);

            if (signedIn && !fallback.Completed)
            {
                // Keep ownership for delayed/duplicate invokes; no tokens or magic codes are stored.
                // The cooldown limits new cards, not how long an existing card can be completed.
                fallback.Completed = true;
                await WriteFallbackAsync(storageKey, fallback, cancellationToken);
                await turnContext.SendActivityAsync(SignedInText, cancellationToken: cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Failed to reply to manual sign-in verification. ErrorType={ErrorType}", exception.GetType().Name);
        }
    }

    private static bool TryGetState(object? value, out string? state)
    {
        state = null;
        if (value is null)
        {
            return false;
        }

        var elements = ProtocolJsonSerializer.ToJsonElements(value);
        if (!elements.TryGetValue("state", out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        state = element.GetString();
        return !string.IsNullOrWhiteSpace(state);
    }

    private static Task<ResourceResponse> SendInvokeResponseAsync(
        ITurnContext context, HttpStatusCode status, CancellationToken cancellationToken) =>
        context.SendActivityAsync(new Activity
        {
            Type = ActivityTypes.InvokeResponse,
            Value = new InvokeResponse { Status = (int)status },
        }, cancellationToken);

    private async Task ResetSdkStateAsync(ITurnContext context, CancellationToken cancellationToken)
    {
        var activity = context.Activity;
        var channel = activity.ChannelId.Channel;
        List<string> keys = [$"oauth/{channel}/{activity.From.Id}/userAuthorizationState"];
        // SDK 1.6's flow record is conversation-wide. Never delete another group member's flow.
        if (string.Equals(activity.Conversation.ConversationType, "personal", StringComparison.OrdinalIgnoreCase))
        {
            keys.Add($"oauth/{_teams.AuthorizationHandler}/{channel}/{activity.Conversation.Id}/flowState");
        }
        await _storage.DeleteAsync([.. keys], cancellationToken);
    }

    private async Task<FallbackState?> ReadFallbackAsync(string key, CancellationToken cancellationToken)
    {
        var records = await _storage.ReadAsync([key], cancellationToken);
        return records.TryGetValue(key, out var value) ? value as FallbackState : null;
    }

    private Task WriteFallbackAsync(string key, FallbackState state, CancellationToken cancellationToken) =>
        _storage.WriteAsync(new Dictionary<string, object> { [key] = state }, cancellationToken);

    private string GetStorageKey(IActivity activity) =>
        $"teams-sso-fallback/{_teams.AuthorizationHandler}/{activity.ChannelId}/{activity.Conversation?.Id}/{activity.From?.Id}";

    internal sealed class FallbackState
    {
        public DateTimeOffset CardSentAt { get; set; }

        public bool Completed { get; set; }
    }
}
