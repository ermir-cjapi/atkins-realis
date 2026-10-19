using System.Text;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Builder.UserAuth;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Contracts;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Receives authenticated Microsoft Teams message activities and delegates them to the HR agent.
/// </summary>
public sealed class HrTeamsAgent : AgentApplication
{
    /// <summary>The authorization policy used by the Bot Connector endpoint.</summary>
    public const string AuthorizationPolicyName = "bot-connector";

    /// <summary>Conversation-scoped state path holding the number of accepted user messages.</summary>
    internal const string MessageCountStatePath = "conversation.hrUserMessageCount";

    /// <summary>Conversation-scoped state path holding recent exchanges for personal chats.</summary>
    internal const string HistoryStatePath = "conversation.hrHistory";

    /// <summary>The command that clears the stored conversation history.</summary>
    internal const string ResetCommand = "/reset";

    /// <summary>The reply sent after the conversation history is cleared.</summary>
    internal const string ResetReply = "I've cleared our conversation history. What would you like to ask?";

    private readonly IAgentChatService _chatService;
    private readonly ChatTurnFactory _turnFactory;
    private readonly AgentOptions _options;
    private readonly TeamsOptions _teamsOptions;
    private readonly ITeamsUserTokenValidator _tokenValidator;
    private readonly ITeamsUserContextAccessor _userContextAccessor;
    private readonly ILogger<HrTeamsAgent> _logger;

    /// <summary>Creates the Teams agent and registers its message handler.</summary>
    public HrTeamsAgent(
        AgentApplicationOptions options,
        IAgentChatService chatService,
        ChatTurnFactory turnFactory,
        IOptions<AgentOptions> agentOptions,
        IOptions<TeamsOptions> teamsOptions,
        ITeamsUserTokenValidator tokenValidator,
        ITeamsUserContextAccessor userContextAccessor,
        ILogger<HrTeamsAgent> logger)
        : base(options)
    {
        _chatService = chatService;
        _turnFactory = turnFactory;
        _options = agentOptions.Value;
        _teamsOptions = teamsOptions.Value;
        _tokenValidator = tokenValidator;
        _userContextAccessor = userContextAccessor;
        _logger = logger;
        OnActivity(
            "message",
            OnMessageAsync,
            autoSignInHandlers: _teamsOptions.SsoEnabled
                ? [_teamsOptions.AuthorizationHandler]
                : []);

        if (_teamsOptions.SsoEnabled)
        {
            if (options.UserAuthorization is null)
            {
                _logger.LogWarning(
                    "Teams SSO is enabled but AgentApplication user authorization is not configured; handler {Handler} cannot sign users in",
                    _teamsOptions.AuthorizationHandler);
            }
            else
            {
                UserAuthorization.OnUserSignInFailure(OnUserSignInFailureAsync);
            }
        }
    }

    // Temporary SSO diagnostic: the Agents SDK only reports sign-in failures through this callback.
    // Registering it replaces the SDK's default failure reply, so the configured reply is sent here.
    private async Task OnUserSignInFailureAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        string handlerName,
        SignInResponse response,
        IActivity initiatingActivity,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            response.Error,
            "Teams sign-in failed. Handler={Handler} Status={Status} Cause={Cause} ErrorType={ErrorType} " +
            "ErrorMessage={ErrorMessage} ActivityType={ActivityType} ActivityName={ActivityName} " +
            "InitiatingActivityType={InitiatingActivityType} ConversationType={ConversationType}",
            handlerName,
            response.Status,
            response.Cause,
            response.Error?.GetType().Name,
            response.Error?.Message,
            turnContext.Activity.Type,
            turnContext.Activity.Name,
            initiatingActivity?.Type,
            turnContext.Activity.Conversation?.ConversationType);

        var failureActivities = Options.UserAuthorization?.SignInFailedMessage?.Invoke(handlerName, response)
            ?? [MessageFactory.Text("SignIn Failed")];
        await turnContext.SendActivitiesAsync(failureActivities, cancellationToken);
    }

    private async Task OnMessageAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["ChannelId"] = turnContext.Activity.ChannelId,
            ["ActivityId"] = turnContext.Activity.Id,
            ["TurnTraceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            ["ConversationType"] = turnContext.Activity.Conversation?.ConversationType,
            ["TenantId"] = turnContext.Activity.Conversation?.TenantId,
            ["SsoEnabled"] = _teamsOptions.SsoEnabled,
            ["AuthorizationHandler"] = _teamsOptions.AuthorizationHandler,
        });

        try
        {
            if (string.Equals(turnContext.Activity.Text?.Trim(), ResetCommand, StringComparison.OrdinalIgnoreCase))
            {
                turnState.DeleteValue(HistoryStatePath);
                await turnContext.SendActivityAsync(ResetReply, cancellationToken: cancellationToken);
                return;
            }

            IDisposable? userScope = null;
            if (_teamsOptions.SsoEnabled)
            {
                _logger.LogInformation(
                    "Teams SSO: requesting delegated token from authorization handler {Handler}",
                    _teamsOptions.AuthorizationHandler);
                string? tokenResponse;
                try
                {
                    tokenResponse = await UserAuthorization.GetTurnTokenAsync(
                        turnContext,
                        _teamsOptions.AuthorizationHandler,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(
                        exception,
                        "Teams SSO: retrieving the delegated token from authorization handler {Handler} threw",
                        _teamsOptions.AuthorizationHandler);
                    throw;
                }

                if (tokenResponse is null)
                {
                    _logger.LogWarning(
                        "No delegated token was available from Teams authorization handler {Handler}",
                        _teamsOptions.AuthorizationHandler);
                }
                else
                {
                    _logger.LogInformation(
                        "Teams SSO: delegated token obtained from authorization handler {Handler}; validating",
                        _teamsOptions.AuthorizationHandler);
                }

                var userContext = tokenResponse is null
                    ? null
                    : await _tokenValidator.ValidateAsync(tokenResponse, cancellationToken);
                if (userContext is null)
                {
                    _logger.LogWarning(
                        "Teams SSO: replying that the work account could not be verified. Reason={Reason}",
                        tokenResponse is null ? "no delegated token" : "delegated token rejected");
                    await turnContext.SendActivityAsync(
                        "I couldn't verify your work account. Please sign in again or contact support.",
                        cancellationToken: cancellationToken);
                    return;
                }

                _logger.LogInformation(
                    "Teams SSO: verified user identity established for the turn. HasUpn={HasUpn}",
                    userContext.Upn is not null);
                userScope = _userContextAccessor.Push(userContext);
            }

            using (userScope)
            {
                var text = turnContext.Activity.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    await turnContext.SendActivityAsync(
                        "Please enter a question for Assist | HR.",
                        cancellationToken: cancellationToken);
                    return;
                }

                if (text.Length > _options.MaxInputCharacters)
                {
                    await turnContext.SendActivityAsync(
                        $"Your message is too long. Please keep it under {_options.MaxInputCharacters} characters.",
                        cancellationToken: cancellationToken);
                    return;
                }

                // Show "typing" in Teams while the agent works; the SDK stops it when the reply is sent.
                StartTypingTimer(turnContext);
                var turn = await CreateTurnAsync(turnContext, turnState, text, cancellationToken);
                var response = await _chatService.GetResponseAsync(turn, cancellationToken);
                var reply = CreateTeamsResponse(response);
                await turnContext.SendActivityAsync(reply, cancellationToken: cancellationToken);
                if (turn.History is not null)
                {
                    turnState.SetValue(
                        HistoryStatePath,
                        ConversationHistory.Serialize(FitHistory([.. turn.History, new(text, reply)])));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Teams agent turn failed");
            try
            {
                await turnContext.SendActivityAsync(
                    "I couldn't process that request. Please try again.",
                    cancellationToken: cancellationToken);
            }
            catch (Exception sendException)
            {
                _logger.LogError(sendException, "Failed to send the Teams turn failure response");
            }
        }
    }

    private async Task<ChatTurn> CreateTurnAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        string text,
        CancellationToken cancellationToken)
    {
        // Teams does not send a user-message index, so the agent keeps one in durable
        // conversation state. It is saved with the rest of the turn state after the handler.
        var messageIndex = turnState.GetValue(MessageCountStatePath, static () => 0) + 1;
        turnState.SetValue(MessageCountStatePath, messageIndex);

        var activity = turnContext.Activity;
        var request = new ChatRequest(
            text,
            activity.Conversation?.Id ?? string.Empty,
            string.IsNullOrWhiteSpace(activity.Id) ? Guid.NewGuid().ToString("N") : activity.Id,
            messageIndex,
            IsConversationStart: messageIndex == 1);
        var turn = await _turnFactory.CreateAsync(request, cancellationToken);

        // Only personal chats keep history: group chats and channels share conversation state
        // between members, so one person's questions must not shape another person's answers.
        if (!KeepsHistory(activity))
        {
            return turn;
        }

        var stored = ConversationHistory.Deserialize(turnState.GetValue<string?>(HistoryStatePath, static () => null));
        return turn with { History = FitHistory(stored) };
    }

    private bool KeepsHistory(IActivity activity) =>
        _options.HistoryMaxExchanges > 0
        && string.Equals(activity.Conversation?.ConversationType, "personal", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<ConversationExchange> FitHistory(IEnumerable<ConversationExchange> exchanges) =>
        ConversationHistory.Fit(exchanges, _options.HistoryMaxExchanges, _options.HistoryMaxCharacters);

    private static string CreateTeamsResponse(ChatResponse response)
    {
        if (response.Citations is not { Count: > 0 })
        {
            return response.Message;
        }

        var builder = new StringBuilder(response.Message);
        builder.AppendLine().AppendLine().AppendLine("Sources:");
        foreach (var citation in response.Citations)
        {
            var title = citation.Title.Replace('\r', ' ').Replace('\n', ' ').Trim();
            builder.Append("- ").Append(title).Append(": ").AppendLine(citation.Url);
        }

        return builder.ToString().TrimEnd();
    }
}