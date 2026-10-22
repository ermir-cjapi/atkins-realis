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

    /// <summary>
    /// Conversation-scoped state path holding the most recent email draft for personal chats.
    /// Allows Teams to simulate the regenerate flow that the web client achieves via client state.
    /// </summary>
    internal const string LastEmailDraftStatePath = "conversation.hrLastEmailDraft";

    /// <summary>
    /// Conversation-scoped state path holding the most recent SMART objective for personal chats.
    /// Allows Teams to simulate the regenerate flow that the web client achieves via client state.
    /// </summary>
    internal const string LastSmartObjectiveStatePath = "conversation.hrLastSmartObjective";

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
                        "Teams SSO: delegated token obtained from authorization handler {Handler}; validating. TokenSource={TokenSource}",
                        _teamsOptions.AuthorizationHandler,
                        TeamsSsoDiagnosticsMiddleware.CurrentInboundActivityName switch
                        {
                            SignInConstants.TokenExchangeOperationName => "sso_exchange",
                            SignInConstants.VerifyStateOperationName => "manual_signin",
                            null => "stored_token",
                            var other => other,
                        });
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

                // Persist state only for personal chats (group/channel state is shared between members).
                if (KeepsHistory(turnContext.Activity))
                {
                    // History: append the new exchange and trim to budget.
                    turnState.SetValue(
                        HistoryStatePath,
                        ConversationHistory.Serialize(FitHistory([.. turn.History ?? [], new(text, reply)])));

                    // Draft / objective: always overwrite so the stored value tracks the latest response.
                    // This lets the user keep regenerating ("make it shorter", "more formal") without any
                    // extra commands — the server holds the structured payload the web client would normally
                    // carry in its own state. Clear the slot when the response had no structured output so
                    // that an unrelated follow-up question does not inadvertently re-use an old draft.
                    if (response.EmailDraft is not null)
                    {
                        turnState.SetValue(LastEmailDraftStatePath,
                            ConversationHistory.SerializeValue(response.EmailDraft));
                        turnState.DeleteValue(LastSmartObjectiveStatePath);
                    }
                    else if (response.SmartObjective is not null)
                    {
                        turnState.SetValue(LastSmartObjectiveStatePath,
                            ConversationHistory.SerializeValue(response.SmartObjective));
                        turnState.DeleteValue(LastEmailDraftStatePath);
                    }
                    else
                    {
                        turnState.DeleteValue(LastEmailDraftStatePath);
                        turnState.DeleteValue(LastSmartObjectiveStatePath);
                    }
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

        // Only personal chats keep state: group chats and channels share conversation state
        // between members, so one person's questions must not shape another person's answers.
        if (!KeepsHistory(activity))
        {
            var request = new ChatRequest(
                text,
                activity.Conversation?.Id ?? string.Empty,
                string.IsNullOrWhiteSpace(activity.Id) ? Guid.NewGuid().ToString("N") : activity.Id,
                messageIndex,
                IsConversationStart: messageIndex == 1);
            return await _turnFactory.CreateAsync(request, cancellationToken);
        }

        // For plain text turns the model revises drafts / objectives naturally through conversation
        // history — the inline rendering means the previous draft is already visible in the prior
        // assistant turn.  We do NOT inject PreviousDraft / PreviousObjective here because we
        // cannot distinguish a regenerate request ("make it shorter") from an unrelated question
        // ("how many days of annual leave do I get?"), and forcing the regenerate path on every
        // follow-up message would break ordinary Q&A after a draft was shown.
        //
        // The structured draft / objective IS stored in state (LastEmailDraftStatePath /
        // LastSmartObjectiveStatePath) for use by future explicit Adaptive Card actions, where
        // a button click gives us certainty of the user's intent.
        var personalRequest = new ChatRequest(
            text,
            activity.Conversation?.Id ?? string.Empty,
            string.IsNullOrWhiteSpace(activity.Id) ? Guid.NewGuid().ToString("N") : activity.Id,
            messageIndex,
            IsConversationStart: messageIndex == 1);

        var stored = ConversationHistory.Deserialize(
            turnState.GetValue<string?>(HistoryStatePath, static () => null));
        var personalTurn = await _turnFactory.CreateAsync(personalRequest, cancellationToken);
        return personalTurn with { History = FitHistory(stored) };
    }

    private bool KeepsHistory(IActivity activity) =>
        _options.HistoryMaxExchanges > 0
        && string.Equals(activity.Conversation?.ConversationType, "personal", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<ConversationExchange> FitHistory(IEnumerable<ConversationExchange> exchanges) =>
        ConversationHistory.Fit(exchanges, _options.HistoryMaxExchanges, _options.HistoryMaxCharacters);

    internal static string CreateTeamsResponse(ChatResponse response)
    {
        var builder = new StringBuilder(response.Message);

        // Render structured email draft as inline Markdown (until Adaptive Cards are available).
        // Teams collapses single newlines, so every line is emitted as its own paragraph, and the
        // closing rule is preceded by a blank line so the last body line is not a setext heading.
        if (response.EmailDraft is { } draft)
        {
            builder.AppendLine().AppendLine();
            builder.AppendLine("---");
            builder.AppendLine("📧 **Email Draft**");
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(draft.SuggestedRecipient))
            {
                builder.Append("**To:** ").AppendLine(draft.SuggestedRecipient);
                builder.AppendLine();
            }
            builder.Append("**Subject:** ").AppendLine(draft.Subject);
            builder.AppendLine();
            foreach (var line in draft.Body.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                builder.AppendLine(line).AppendLine();
            }
            builder.Append("---");
        }

        // Render structured SMART objective as inline Markdown (until Adaptive Cards are available).
        if (response.SmartObjective is { } objective)
        {
            builder.AppendLine().AppendLine();
            builder.Append("🎯 **SMART Objective:** ").Append(objective.ObjectiveText).AppendLine();
            if (!string.IsNullOrWhiteSpace(objective.Specific))
            {
                builder.AppendLine();
                builder.Append("📌 **Specific:** ").AppendLine(objective.Specific);
            }
            if (!string.IsNullOrWhiteSpace(objective.Measurable))
            {
                builder.AppendLine();
                builder.Append("📊 **Measurable:** ").AppendLine(objective.Measurable);
            }
            if (!string.IsNullOrWhiteSpace(objective.Achievable))
            {
                builder.AppendLine();
                builder.Append("✅ **Achievable:** ").AppendLine(objective.Achievable);
            }
            if (!string.IsNullOrWhiteSpace(objective.Relevant))
            {
                builder.AppendLine();
                builder.Append("💼 **Relevant:** ").AppendLine(objective.Relevant);
            }
            if (!string.IsNullOrWhiteSpace(objective.TimeBound))
            {
                builder.AppendLine();
                builder.Append("📅 **Time-Bound:** ").AppendLine(objective.TimeBound);
            }
        }

        // Append citation sources.
        if (response.Citations is { Count: > 0 })
        {
            builder.AppendLine().AppendLine().AppendLine("Sources:");
            foreach (var citation in response.Citations)
            {
                var title = citation.Title.Replace('\r', ' ').Replace('\n', ' ').Trim();
                builder.Append("- ").Append(title).Append(": ").AppendLine(citation.Url);
            }
        }

        return builder.ToString().TrimEnd();
    }
}