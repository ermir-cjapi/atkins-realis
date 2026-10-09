using System.Diagnostics;
using System.Text;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
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
            ["TurnTraceId"] = Activity.Current?.TraceId.ToString(),
        });

        try
        {
            IDisposable? userScope = null;
            if (_teamsOptions.SsoEnabled)
            {
                var tokenResponse = await UserAuthorization.GetTurnTokenAsync(
                    turnContext,
                    _teamsOptions.AuthorizationHandler,
                    cancellationToken);
                var userContext = tokenResponse is null
                    ? null
                    : await _tokenValidator.ValidateAsync(tokenResponse, cancellationToken);
                if (userContext is null)
                {
                    await turnContext.SendActivityAsync(
                        "I couldn't verify your work account. Please sign in again or contact support.",
                        cancellationToken: cancellationToken);
                    return;
                }

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

                var response = await _chatService.GetResponseAsync(
                    await CreateTurnAsync(turnContext, turnState, text, cancellationToken),
                    cancellationToken);
                await turnContext.SendActivityAsync(
                    CreateTeamsResponse(response),
                    cancellationToken: cancellationToken);
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
        return await _turnFactory.CreateAsync(request, cancellationToken);
    }

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