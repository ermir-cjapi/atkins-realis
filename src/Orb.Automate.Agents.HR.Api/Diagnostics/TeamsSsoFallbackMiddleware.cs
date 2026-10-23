using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.UserAuth.TokenService;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// When the Teams client reports that silent SSO failed (<c>signin/failure</c>), the Agents SDK ends the
/// turn without replying. This adapter middleware then sends a manual sign-in OAuth card (no token
/// exchange resource) so the user gets a sign-in button instead of silence. At most one card is sent per
/// user and conversation within <see cref="CooldownPeriod"/>. Never throws and never logs the sign-in link.
/// </summary>
public sealed class TeamsSsoFallbackMiddleware : Microsoft.Agents.Builder.IMiddleware
{
    /// <summary>The text shown on the fallback sign-in card.</summary>
    internal const string FallbackText =
        "Silent sign-in didn't work. Please sign in with the button below, then send your question again.";

    /// <summary>The minimum interval between fallback cards for the same user and conversation.</summary>
    internal static readonly TimeSpan CooldownPeriod = TimeSpan.FromMinutes(15);

    private const string SignInButtonTitle = "Sign in";

    private readonly TeamsOptions _teams;
    private readonly string? _connectionName;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TeamsSsoFallbackMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    public TeamsSsoFallbackMiddleware(
        IOptions<TeamsOptions> teamsOptions,
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<TeamsSsoFallbackMiddleware> logger)
    {
        _teams = teamsOptions.Value;
        _connectionName = configuration[
            $"AgentApplication:UserAuthorization:Handlers:{_teams.AuthorizationHandler}:Settings:AzureBotOAuthConnectionName"];
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnTurnAsync(ITurnContext turnContext, NextDelegate next, CancellationToken cancellationToken)
    {
        await next(cancellationToken);

        var activity = turnContext.Activity;
        if (!_teams.SsoEnabled ||
            !string.Equals(activity.Type, ActivityTypes.Invoke, StringComparison.OrdinalIgnoreCase) ||
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

            var cacheKey = $"teams-sso-fallback:{activity.Conversation?.Id}:{activity.From?.Id}";
            if (_cache.TryGetValue(cacheKey, out _))
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
            await turnContext.SendActivityAsync(
                new Activity
                {
                    Type = ActivityTypes.Message,
                    Attachments = [new Attachment { ContentType = OAuthCard.ContentType, Content = card }],
                },
                cancellationToken);
            _cache.Set(cacheKey, true, CooldownPeriod);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Failed to send manual sign-in fallback card");
        }
    }
}
