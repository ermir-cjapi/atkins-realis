using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// Temporary Teams SSO diagnostic: logs inbound sign-in traffic and outbound sign-in cards and
/// invoke responses for every Bot Connector turn. It runs as adapter middleware, ahead of
/// <c>AgentApplication</c>, so it also observes sign-in invokes the Agents SDK consumes before
/// routing. Never logs tokens, magic codes, sign-in links, UPNs or message text. The user's Entra
/// object ID is attached to every log in the turn (as a scope) so a user's sign-in path can be traced
/// and affected users identified.
/// </summary>
public sealed class TeamsSsoDiagnosticsMiddleware(ILogger<TeamsSsoDiagnosticsMiddleware> logger) : Microsoft.Agents.Builder.IMiddleware
{
    private const int MaxLoggedBodyCharacters = 2000;
    private const string SignInInvokePrefix = "signin/";

    private static readonly AsyncLocal<string?> InboundActivityName = new();

    /// <summary>
    /// The name of the activity Teams sent for the current turn. The Agents SDK may replay the original
    /// message after a sign-in invoke, so handlers use this to tell how the user's token was obtained.
    /// </summary>
    internal static string? CurrentInboundActivityName => InboundActivityName.Value;

    /// <inheritdoc />
    public async Task OnTurnAsync(ITurnContext turnContext, NextDelegate next, CancellationToken cancellationToken)
    {
        var activity = turnContext.Activity;
        InboundActivityName.Value = activity.Name;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ChannelId"] = activity.ChannelId?.ToString(),
            ["ActivityId"] = activity.Id,
            ["ActivityType"] = activity.Type,
            ["ActivityName"] = activity.Name,
            ["ConversationType"] = activity.Conversation?.ConversationType,
            ["AadObjectId"] = activity.From?.AadObjectId,
        });

        logger.LogInformation(
            "Teams inbound activity. Type={ActivityType} Name={ActivityName} ConversationType={ConversationType} " +
            "TenantId={TenantId} HasAadObjectId={HasAadObjectId} ServiceUrlHost={ServiceUrlHost} " +
            "HasText={HasText} ReplyToId={ReplyToId}",
            activity.Type,
            activity.Name,
            activity.Conversation?.ConversationType,
            activity.Conversation?.TenantId,
            !string.IsNullOrWhiteSpace(activity.From?.AadObjectId),
            Uri.TryCreate(activity.ServiceUrl, UriKind.Absolute, out var serviceUrl) ? serviceUrl.Host : null,
            !string.IsNullOrWhiteSpace(activity.Text),
            activity.ReplyToId);
        LogSignInRequest(activity);

        var sentVisibleMessage = false;
        var sentSignInCard = false;
        turnContext.OnSendActivities(async (context, activities, sendNext) =>
        {
            foreach (var outgoing in activities)
            {
                LogOutgoing(activity, outgoing);
                if (string.Equals(outgoing.Type, ActivityTypes.Message, StringComparison.OrdinalIgnoreCase))
                {
                    if (outgoing.Attachments?.Any(IsOAuthCard) == true)
                    {
                        sentSignInCard = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(outgoing.Text) || outgoing.Attachments is { Count: > 0 })
                    {
                        sentVisibleMessage = true;
                    }
                }
            }

            try
            {
                return await sendNext();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(
                    exception,
                    "Sending Teams activities failed. OutgoingTypes={OutgoingTypes}",
                    string.Join(',', activities.Select(static item => item.Type)));
                throw;
            }
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(cancellationToken);
            var outcome = activity.Name == SignInConstants.SignInFailure ? "signin_failed"
                : sentVisibleMessage ? "replied"
                : sentSignInCard ? "signin_pending"
                : string.Equals(activity.Type, ActivityTypes.Message, StringComparison.OrdinalIgnoreCase) ? "silent"
                : "no_reply";
            logger.Log(
                outcome is "signin_failed" or "silent" ? LogLevel.Warning : LogLevel.Information,
                "Teams turn completed. Type={ActivityType} Name={ActivityName} Outcome={TurnOutcome} " +
                "SentVisibleMessage={SentVisibleMessage} SentSignInCard={SentSignInCard} ElapsedMs={ElapsedMs}",
                activity.Type,
                activity.Name,
                outcome,
                sentVisibleMessage,
                sentSignInCard,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Teams turn failed. Type={ActivityType} Name={ActivityName} ElapsedMs={ElapsedMs}",
                activity.Type,
                activity.Name,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private void LogSignInRequest(IActivity activity)
    {
        var isInvoke = string.Equals(activity.Type, ActivityTypes.Invoke, StringComparison.OrdinalIgnoreCase);
        var isEvent = string.Equals(activity.Type, ActivityTypes.Event, StringComparison.OrdinalIgnoreCase);

        if (isInvoke && activity.Name == SignInConstants.TokenExchangeOperationName)
        {
            var value = ReadJson(activity.Value);
            var token = GetString(value, "token");
            logger.LogInformation(
                "Teams SSO token exchange request received. ExchangeId={ExchangeId} ConnectionName={ConnectionName} HasToken={HasToken}",
                GetString(value, "id"),
                GetString(value, "connectionName"),
                !string.IsNullOrEmpty(token));
            LogTokenSummary("Teams SSO token supplied by the Teams client", token);
        }
        else if (isInvoke && activity.Name == SignInConstants.SignInFailure)
        {
            var value = ReadJson(activity.Value);
            var clientInfo = ReadJson(activity.Entities?.FirstOrDefault(static entity =>
                string.Equals(entity.Type, "clientInfo", StringComparison.OrdinalIgnoreCase)));
            logger.LogWarning(
                "Teams client reported an SSO failure. Code={FailureCode} Message={FailureMessage} AadObjectId={AadObjectId} " +
                "ClientPlatform={ClientPlatform} ClientLocale={ClientLocale}",
                GetString(value, "code"),
                GetString(value, "message"),
                activity.From?.AadObjectId,
                GetString(clientInfo, "platform"),
                GetString(clientInfo, "locale") ?? activity.Locale);
        }
        else if (isInvoke && activity.Name == SignInConstants.VerifyStateOperationName)
        {
            var value = ReadJson(activity.Value);
            logger.LogInformation(
                "Teams sign-in verifyState received (interactive sign-in completed in the browser). HasState={HasState}",
                !string.IsNullOrEmpty(GetString(value, "state")));
        }
        else if (isEvent && activity.Name == SignInConstants.TokenResponseEventName)
        {
            var value = ReadJson(activity.Value);
            var token = GetString(value, "token");
            logger.LogInformation(
                "Token response event received. ConnectionName={ConnectionName} HasToken={HasToken}",
                GetString(value, "connectionName"),
                !string.IsNullOrEmpty(token));
            LogTokenSummary("Token response event token", token);
        }
        else if (isInvoke && activity.Name?.StartsWith(SignInInvokePrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            logger.LogInformation("Unrecognised Teams sign-in invoke received. Name={ActivityName}", activity.Name);
        }
    }

    private void LogOutgoing(IActivity inbound, IActivity outgoing)
    {
        if (string.Equals(outgoing.Type, ActivityTypes.InvokeResponse, StringComparison.OrdinalIgnoreCase))
        {
            var response = outgoing.Value as InvokeResponse;
            var status = response?.Status;
            var body = inbound.Name?.StartsWith(SignInInvokePrefix, StringComparison.OrdinalIgnoreCase) == true
                ? Truncate(ReadJson(response?.Body)?.GetRawText())
                : null;
            logger.Log(
                status is null or >= 400 ? LogLevel.Warning : LogLevel.Information,
                "Teams invoke response prepared. InvokeName={InvokeName} Status={Status} Body={Body}",
                inbound.Name,
                status,
                body);
            return;
        }

        foreach (var attachment in outgoing.Attachments ?? [])
        {
            if (IsOAuthCard(attachment))
            {
                LogOAuthCard(attachment);
            }
        }

        logger.LogInformation(
            "Teams outbound activity. Type={OutgoingType} AttachmentContentTypes={AttachmentContentTypes} TextLength={TextLength}",
            outgoing.Type,
            string.Join(',', (outgoing.Attachments ?? []).Select(static attachment => attachment.ContentType)),
            outgoing.Text?.Length ?? 0);
    }

    private static bool IsOAuthCard(Attachment attachment) =>
        string.Equals(attachment.ContentType, OAuthCard.ContentType, StringComparison.OrdinalIgnoreCase);

    private void LogOAuthCard(Attachment attachment)
    {
        var card = ReadJson(attachment.Content);
        var exchange = GetObject(card, "tokenExchangeResource");
        var exchangeUri = GetString(exchange, "uri");
        logger.LogInformation(
            "Sending OAuth sign-in card. ConnectionName={ConnectionName} HasTokenExchangeResource={HasTokenExchangeResource} " +
            "TokenExchangeResourceId={TokenExchangeResourceId} TokenExchangeResourceUri={TokenExchangeResourceUri} " +
            "HasTokenPostResource={HasTokenPostResource} ButtonCount={ButtonCount}",
            GetString(card, "connectionName"),
            exchange is not null,
            GetString(exchange, "id"),
            exchangeUri,
            GetObject(card, "tokenPostResource") is not null,
            card is { ValueKind: JsonValueKind.Object } && card.Value.TryGetProperty("buttons", out var buttons) &&
                buttons.ValueKind == JsonValueKind.Array
                ? buttons.GetArrayLength()
                : 0);

        if (string.IsNullOrWhiteSpace(exchangeUri))
        {
            logger.LogWarning(
                "OAuth sign-in card has no token exchange resource URI, so Teams cannot attempt silent SSO and will show " +
                "the sign-in button. Check the Azure Bot OAuth connection's Token Exchange URL.");
        }
    }

    private void LogTokenSummary(string source, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        if (!JwtDiagnosticSummary.TryRead(token, out var summary, out var failure))
        {
            logger.LogWarning("{TokenSource} could not be read: {Reason}", source, failure);
            return;
        }

        logger.LogInformation(
            "{TokenSource}. Audience={Audience} Issuer={Issuer} Version={Version} Scopes={Scopes} " +
            "ClientAppId={ClientAppId} TenantId={TenantId} ExpiresUtc={ExpiresUtc}",
            source,
            summary!.Audience,
            summary.Issuer,
            summary.Version,
            summary.Scopes,
            summary.ClientAppId,
            summary.TenantId,
            summary.ExpiresUtc);
    }

    private static JsonElement? ReadJson(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return value is JsonElement element ? element : ProtocolJsonSerializer.ToJsonElement(value);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonElement? GetObject(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    private static string? GetString(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? Truncate(string? value) =>
        value is { Length: > MaxLoggedBodyCharacters } ? value[..MaxLoggedBodyCharacters] : value;
}

/// <summary>
/// Temporary Teams SSO diagnostic: logs the effective, non-secret SSO configuration at startup so
/// mismatches between the bot, OAuth connection and HR API registration are visible in telemetry.
/// </summary>
public static class TeamsSsoConfigurationLogger
{
    /// <summary>Logs the effective Teams SSO configuration and warns about obvious gaps.</summary>
    public static void Log(
        ILogger logger,
        IConfiguration configuration,
        TeamsOptions teams,
        EntraAuthenticationOptions entra)
    {
        var userAuthorization = configuration.GetSection("AgentApplication:UserAuthorization");
        var handlerSettings = userAuthorization.GetSection($"Handlers:{teams.AuthorizationHandler}:Settings");
        var connectionName = handlerSettings["AzureBotOAuthConnectionName"];
        var serviceConnection = configuration.GetSection("Connections:ServiceConnection:Settings");
        var onBehalfOf = configuration.GetSection(OnBehalfOfOptions.SectionName);

        logger.LogInformation(
            "Teams SSO configuration. TeamsEnabled={TeamsEnabled} SsoEnabled={SsoEnabled} BotAppId={BotAppId} " +
            "BotTenantId={BotTenantId} AuthorizationHandler={AuthorizationHandler} HandlerConfigured={HandlerConfigured} " +
            "OAuthConnectionName={OAuthConnectionName} HandlerEnableSso={HandlerEnableSso} " +
            "HandlerOboConnectionName={HandlerOboConnectionName} AutoSignIn={AutoSignIn} " +
            "DefaultHandlerName={DefaultHandlerName} ServiceConnectionAuthType={ServiceConnectionAuthType} " +
            "ServiceConnectionClientId={ServiceConnectionClientId} EntraTenantId={EntraTenantId} " +
            "EntraAudience={EntraAudience} RequiredScope={RequiredScope} OboUserTokenMode={OboUserTokenMode} " +
            "OboClientId={OboClientId} HasStorageContainer={HasStorageContainer}",
            teams.Enabled,
            teams.SsoEnabled,
            teams.AppId,
            teams.TenantId,
            teams.AuthorizationHandler,
            handlerSettings.Exists(),
            connectionName,
            handlerSettings["EnableSso"],
            handlerSettings["OBOConnectionName"],
            userAuthorization["AutoSignIn"],
            userAuthorization["DefaultHandlerName"],
            serviceConnection["AuthType"],
            serviceConnection["ClientId"],
            entra.TenantId,
            entra.Audience,
            entra.RequiredScope,
            onBehalfOf[nameof(OnBehalfOfOptions.UserTokenMode)],
            onBehalfOf[nameof(OnBehalfOfOptions.ClientId)],
            !string.IsNullOrWhiteSpace(teams.StorageContainerUri));

        if (!teams.SsoEnabled)
        {
            return;
        }

        if (!handlerSettings.Exists() || string.IsNullOrWhiteSpace(connectionName))
        {
            logger.LogWarning(
                "Teams SSO is enabled but authorization handler {AuthorizationHandler} has no AzureBotOAuthConnectionName",
                teams.AuthorizationHandler);
        }

        if (!string.IsNullOrWhiteSpace(serviceConnection["ClientId"]) &&
            !string.Equals(serviceConnection["ClientId"], teams.AppId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Bot service connection client ID {ServiceConnectionClientId} differs from Teams:AppId {BotAppId}",
                serviceConnection["ClientId"],
                teams.AppId);
        }
    }
}
