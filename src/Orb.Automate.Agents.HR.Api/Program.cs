using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Authentication.Msal;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.Agents.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Security.Claims;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Endpoints;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.Search;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.ServiceNow;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Scalar.AspNetCore;
using OpenAI.Chat;
using OpenAI.Responses;

using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Logging.AddFilter("System.Net.Http.HttpClient.ServiceNowAiSearch", LogLevel.None);
TokenCredential azureCredential = builder.Environment.IsDevelopment()
    ? new AzureCliCredential()
    : new DefaultAzureCredential();

// The agent name doubles as the DI service key for AddAIAgent, so it must be read straight from
// configuration here: IOptions<AgentOptions> is not resolvable until after the container is built.
var hostedAgentName = builder.Configuration[$"{AgentOptions.SectionName}:{nameof(AgentOptions.Name)}"] is { Length: > 0 } configuredAgentName
    ? configuredAgentName
    : AgentOptions.DefaultName;

builder.Services.AddOptions<FoundryOptions>()
    .Bind(builder.Configuration.GetSection(FoundryOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(static options => Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
        "Foundry:Endpoint must be an absolute HTTPS URI.")
    .ValidateOnStart();
builder.Services.AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<TelemetryOptions>()
    .Bind(builder.Configuration.GetSection(TelemetryOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<GraphProfileOptions>()
    .Bind(builder.Configuration.GetSection(GraphProfileOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(static options => !options.Enabled || IsHttpsUri(options.BaseUri),
        "GraphProfile:BaseUri must be an absolute HTTPS URI when profile enrichment is enabled.")
    .ValidateOnStart();
builder.Services.AddOptions<EntraAuthenticationOptions>()
    .Bind(builder.Configuration.GetSection(EntraAuthenticationOptions.SectionName))
    .Validate(options => builder.Environment.IsDevelopment() ||
        (!string.IsNullOrWhiteSpace(options.TenantId) &&
        !string.IsNullOrWhiteSpace(options.Audience) &&
        !string.IsNullOrWhiteSpace(options.RequiredScope)),
        "Entra authentication outside Development requires a tenant ID, audience and delegated scope.")
    .ValidateOnStart();
builder.Services.AddOptions<TeamsOptions>()
    .Bind(builder.Configuration.GetSection(TeamsOptions.SectionName))
    .Validate(TeamsOptions.IsValid,
        "Teams identity values and the authorization handler must be valid when Teams SSO is enabled.")
    .Validate(options => builder.Environment.IsDevelopment() ||
        !options.Enabled ||
        IsHttpsUri(options.StorageContainerUri),
        "Teams outside Development requires an absolute HTTPS durable storage container URI.")
    .ValidateOnStart();

var configuredEntraAuthentication = builder.Configuration
    .GetSection(EntraAuthenticationOptions.SectionName)
    .Get<EntraAuthenticationOptions>() ?? new EntraAuthenticationOptions();
var entraTenantId = string.IsNullOrWhiteSpace(configuredEntraAuthentication.TenantId)
    ? "organizations"
    : configuredEntraAuthentication.TenantId;
var entraAudience = string.IsNullOrWhiteSpace(configuredEntraAuthentication.Audience)
    ? "api://orb-automate-hr-local"
    : configuredEntraAuthentication.Audience;
var entraRequiredScope = string.IsNullOrWhiteSpace(configuredEntraAuthentication.RequiredScope)
    ? "access_as_user"
    : configuredEntraAuthentication.RequiredScope;
var configuredTeams = builder.Configuration
    .GetSection(TeamsOptions.SectionName)
    .Get<TeamsOptions>() ?? new TeamsOptions();

var authentication = builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = AuthenticationSchemes.HrApi;
        options.DefaultChallengeScheme = AuthenticationSchemes.HrApi;
    })
    .AddJwtBearer(AuthenticationSchemes.HrApi, options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{entraTenantId}/v2.0";
        options.Audience = entraAudience;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidAudience = entraAudience,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    identity.AddClaim(new Claim(
                        AuthenticationSchemes.PrincipalSourceClaim,
                        AuthenticationSchemes.HrApi));
                }

                return Task.CompletedTask;
            },
        };
    });
if (configuredTeams.Enabled)
{
    authentication.AddJwtBearer(AuthenticationSchemes.BotConnector, options =>
    {
        options.MetadataAddress = AuthenticationConstants.PublicAzureBotServiceOpenIdMetadataUrl;
        options.Audience = configuredTeams.AppId;
        options.RequireHttpsMetadata = true;
        options.SaveToken = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidIssuer = AuthenticationConstants.BotFrameworkTokenIssuer,
            ValidAudience = configuredTeams.AppId,
            ClockSkew = TimeSpan.FromMinutes(5),
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    identity.AddClaim(new Claim(
                        AuthenticationSchemes.PrincipalSourceClaim,
                        AuthenticationSchemes.BotConnector));
                }

                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                GetBotConnectorLogger(context.HttpContext).LogWarning(
                    context.Exception,
                    "Bot Connector token validation failed: {FailureType}. Path={Path} ExpectedAudience={ExpectedAudience}",
                    context.Exception.GetType().Name,
                    context.Request.Path,
                    configuredTeams.AppId);
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                GetBotConnectorLogger(context.HttpContext).LogWarning(
                    "Bot Connector request challenged. Path={Path} HasAuthorizationHeader={HasAuthorizationHeader} " +
                    "Error={Error} ErrorDescription={ErrorDescription}",
                    context.Request.Path,
                    !string.IsNullOrEmpty(context.Request.Headers.Authorization),
                    context.Error,
                    context.ErrorDescription);
                return Task.CompletedTask;
            },
            OnForbidden = context =>
            {
                GetBotConnectorLogger(context.HttpContext).LogWarning(
                    "Bot Connector request forbidden. Path={Path}",
                    context.Request.Path);
                return Task.CompletedTask;
            },
        };
    });
}

var authorization = builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ChatEndpoints.AuthorizationPolicyName, policy => policy
        .AddAuthenticationSchemes(AuthenticationSchemes.HrApi)
        .RequireAuthenticatedUser()
        .RequireAssertion(context => context.User.Claims
            .Where(claim => claim.Type == "scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(entraRequiredScope, StringComparer.Ordinal)));
if (configuredTeams.Enabled)
{
    authorization.AddPolicy(HrTeamsAgent.AuthorizationPolicyName, policy => policy
        .AddAuthenticationSchemes(AuthenticationSchemes.BotConnector)
        .RequireAuthenticatedUser());
}

// Read straight from configuration: the flag decides which services are registered, so it must be
// known before the container is built.
var sharePointSearchEnabled = builder.Configuration.GetValue<bool>(
    $"{SharePointSearchOptions.SectionName}:{nameof(SharePointSearchOptions.Enabled)}");

var serviceNowEnabled = builder.Configuration.GetValue<bool>(
    $"{ServiceNowOptions.SectionName}:{nameof(ServiceNowOptions.Enabled)}");
var graphProfileEnabled = builder.Configuration.GetValue<bool>(
    $"{GraphProfileOptions.SectionName}:{nameof(GraphProfileOptions.Enabled)}");

builder.Services.AddOptions<SharePointSearchOptions>()
    .Bind(builder.Configuration.GetSection(SharePointSearchOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(static options => !options.Enabled ||
        (IsHttpsUri(options.Endpoint) &&
        !string.IsNullOrWhiteSpace(options.IndexName) &&
        !string.IsNullOrWhiteSpace(options.SemanticConfigurationName)),
        "Enabled SharePoint search requires an HTTPS endpoint, an index name and a semantic configuration name.")
    .ValidateOnStart();
builder.Services.AddOptions<OnBehalfOfOptions>()
    .Bind(builder.Configuration.GetSection(OnBehalfOfOptions.SectionName))
    .Validate(options => Enum.IsDefined(options.UserTokenMode),
        "OnBehalfOf:UserTokenMode must be AzureCli or OnBehalfOf.")
    .Validate(options => !(sharePointSearchEnabled || graphProfileEnabled) || builder.Environment.IsDevelopment() ||
        options.UserTokenMode == SearchUserTokenMode.OnBehalfOf,
        "SharePoint search and Graph profile enrichment outside Development require OnBehalfOf:UserTokenMode=OnBehalfOf.")
    .Validate(options => !(sharePointSearchEnabled || graphProfileEnabled) || options.UserTokenMode != SearchUserTokenMode.OnBehalfOf ||
        (!string.IsNullOrWhiteSpace(options.TenantId) && !string.IsNullOrWhiteSpace(options.ClientId)),
        "On-behalf-of user tokens require OnBehalfOf:TenantId and OnBehalfOf:ClientId.")
    .Validate(options => !(sharePointSearchEnabled || graphProfileEnabled) || !builder.Environment.IsDevelopment() ||
        options.UserTokenMode != SearchUserTokenMode.OnBehalfOf || !string.IsNullOrWhiteSpace(options.ClientSecret),
        "Development on-behalf-of user tokens require OnBehalfOf:ClientSecret from user secrets.")
    .Validate(options => builder.Environment.IsDevelopment() || string.IsNullOrWhiteSpace(options.ClientSecret),
        "OnBehalfOf:ClientSecret may only be configured in Development.")
    .ValidateOnStart();
builder.Services.AddOptions<ServiceNowOptions>()
    .Bind(builder.Configuration.GetSection(ServiceNowOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => !options.Enabled ||
        (IsHttpsUri(options.Endpoint) &&
        IsHttpsUri(options.TokenEndpoint) &&
        !string.IsNullOrWhiteSpace(options.ClientId) &&
        !string.IsNullOrWhiteSpace(options.ClientSecret)),
        "Enabled ServiceNow search requires HTTPS endpoint and token endpoint values plus OAuth client credentials.")
    .Validate(options => builder.Environment.IsDevelopment() || string.IsNullOrWhiteSpace(options.DevelopmentUserUpn),
        "ServiceNow:DevelopmentUserUpn may only be configured in Development.")
    .Validate(static options =>
        options.CacheFreshMinutes <= options.CacheMaximumStaleMinutes,
        "ServiceNow:CacheFreshMinutes must not exceed CacheMaximumStaleMinutes.")
    .Validate(static options =>
        options.SearchAttemptTimeoutMilliseconds < options.TurnBudgetMilliseconds &&
        options.OAuthAttemptTimeoutMilliseconds < options.TurnBudgetMilliseconds,
        "ServiceNow per-attempt timeouts must be shorter than TurnBudgetMilliseconds.")
    .Validate(static options =>
        options.CircuitSamplingMilliseconds >= options.SearchAttemptTimeoutMilliseconds * 2,
        "ServiceNow:CircuitSamplingMilliseconds must be at least twice SearchAttemptTimeoutMilliseconds.")
    .Validate(static options =>
        options.RateLimitRequestsPerSecond <= options.RateLimitBurst,
        "ServiceNow:RateLimitRequestsPerSecond must not exceed RateLimitBurst.")
    .ValidateOnStart();

builder.Services.AddSingleton(azureCredential);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITeamsUserContextAccessor, TeamsUserContextAccessor>();
builder.Services.AddSingleton<ITeamsUserTokenValidator, TeamsUserTokenValidator>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ICurrentUserUpnAccessor, CurrentUserUpnAccessor>();
builder.Services.AddSingleton<ICurrentUserProfileProvider, CurrentUserProfileProvider>();
builder.Services.AddSingleton<ChatTurnFactory>();
builder.Services.AddSingleton<IChatTelemetry, ChatTelemetry>();
builder.Services.AddSingleton<IChatAiMetrics, ChatAiMetrics>();
builder.Services.AddSingleton<IIntentClassifier, FoundryIntentClassifier>();
builder.Services.AddSingleton<IGroundingTelemetryContext, GroundingTelemetryContext>();
builder.Services.AddSingleton<IAiTurnTelemetryContext, AiTurnTelemetryContext>();
builder.Services.AddHttpClient(nameof(CurrentUserProfileProvider), static (services, client) =>
{
    var settings = services.GetRequiredService<IOptions<GraphProfileOptions>>().Value;
    client.BaseAddress = new Uri(settings.BaseUri.EndsWith("/", StringComparison.Ordinal)
        ? settings.BaseUri
        : string.Concat(settings.BaseUri, "/"));
    client.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
}).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });

// ServiceNow components also use this fail-closed writer for local diagnostics, so retain the
// dependency when that integration is enabled even though the model payload decorator is
// activated only in Development.
if (serviceNowEnabled || AgentRuntimePolicy.EnablePayloadLogging(builder.Environment))
{
    builder.Services.AddSingleton<DevelopmentPayloadWriter>();
}

if (sharePointSearchEnabled)
{
    builder.Services.AddSingleton<ISearchUserTokenProvider, SearchUserTokenProvider>();
    builder.Services.AddSingleton<ISharePointSearchClient, SharePointSearchClient>();
    builder.Services.AddSingleton<SearchHrPolicyDocumentsTool>();
}

if (serviceNowEnabled)
{
    builder.Services.AddHttpClient(ServiceNowOAuthTokenProvider.HttpClientName, ConfigureServiceNowHttpClient)
        .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddHttpClient(ServiceNowAiSearchClient.HttpClientName, ConfigureServiceNowHttpClient)
        .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddSingleton<IServiceNowOAuthTokenProvider, ServiceNowOAuthTokenProvider>();
    builder.Services.AddSingleton<IServiceNowAiSearchClient, ServiceNowAiSearchClient>();
}

// Unified parallel retrieval tool: registered when at least one knowledge source is enabled.
// Fans out to SharePoint and ServiceNow in parallel, normalises scores, de-duplicates passages,
// and returns one merged ranked list for model consumption.
if (sharePointSearchEnabled || serviceNowEnabled)
{
    builder.Services.AddSingleton(services => new SearchHrKnowledgeTool(
        services.GetRequiredService<IOptions<SharePointSearchOptions>>(),
        services.GetRequiredService<IGroundingTelemetryContext>(),
        services.GetRequiredService<ILogger<SearchHrKnowledgeTool>>(),
        tokenProvider: sharePointSearchEnabled ? services.GetService<ISearchUserTokenProvider>() : null,
        spClient:      sharePointSearchEnabled ? services.GetService<ISharePointSearchClient>() : null,
        // ServiceNowKnowledgeRetriever owns all SN dependencies; created here so SearchHrKnowledgeTool
        // has no direct knowledge of ICurrentUserUpnAccessor or IServiceNowAiSearchClient.
        snRetriever:   serviceNowEnabled
            ? new ServiceNowKnowledgeRetriever(
                services.GetRequiredService<ICurrentUserUpnAccessor>(),
                services.GetRequiredService<IServiceNowAiSearchClient>(),
                services.GetRequiredService<IOptions<ServiceNowOptions>>(),
                services.GetRequiredService<ILogger<ServiceNowKnowledgeRetriever>>())
            : null,
        payloadWriter: services.GetService<DevelopmentPayloadWriter>()));
}
var oauthHttpClient = builder.Services.AddHttpClient(ServiceNowOAuthTokenProvider.HttpClientName, ConfigureServiceNowHttpClient);
#pragma warning disable EXTEXP0001 // Required to replace the inherited global pipeline rather than stack resilience handlers.
oauthHttpClient.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
oauthHttpClient.AddResilienceHandler("ServiceNowOAuth", static (pipeline, context) =>
    {
        var settings = context.ServiceProvider.GetRequiredService<IOptions<ServiceNowOptions>>().Value;
        pipeline
            .AddRetry(CreateRetryOptions(settings.OAuthRetryCount, settings.RetryBaseDelayMilliseconds, "oauth"))
            .AddTimeout(TimeSpan.FromMilliseconds(settings.OAuthAttemptTimeoutMilliseconds));
    });
oauthHttpClient.ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });

var searchHttpClient = builder.Services.AddHttpClient(ServiceNowAiSearchClient.HttpClientName, ConfigureServiceNowHttpClient);
#pragma warning disable EXTEXP0001 // Required to replace the inherited global pipeline rather than stack resilience handlers.
searchHttpClient.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
searchHttpClient.AddResilienceHandler("ServiceNowSearch", static (pipeline, context) =>
    {
        var settings = context.ServiceProvider.GetRequiredService<IOptions<ServiceNowOptions>>().Value;
        pipeline
            .AddRetry(CreateRetryOptions(settings.SearchRetryCount, settings.RetryBaseDelayMilliseconds, "search"))
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = settings.CircuitFailureRatio,
                MinimumThroughput = settings.CircuitMinimumThroughput,
                SamplingDuration = TimeSpan.FromMilliseconds(settings.CircuitSamplingMilliseconds),
                BreakDuration = TimeSpan.FromMilliseconds(settings.CircuitBreakMilliseconds),
                ShouldHandle = CreateTransientPredicate(),
                OnOpened = static _ =>
                {
                    ServiceNowDiagnostics.RecordCircuitTransition("open");
                    return default;
                },
                OnHalfOpened = static _ =>
                {
                    ServiceNowDiagnostics.RecordCircuitTransition("half_open");
                    return default;
                },
                OnClosed = static _ =>
                {
                    ServiceNowDiagnostics.RecordCircuitTransition("closed");
                    return default;
                }
            })
            .AddTimeout(TimeSpan.FromMilliseconds(settings.SearchAttemptTimeoutMilliseconds));
    });
searchHttpClient.AddHttpMessageHandler<ServiceNowRateLimitHandler>();
searchHttpClient.ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(static services =>
{
    var settings = services.GetRequiredService<IOptions<ServiceNowOptions>>().Value;
    return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = settings.RateLimitBurst,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = settings.RateLimitQueueLimit,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        TokensPerPeriod = settings.RateLimitRequestsPerSecond,
        AutoReplenishment = true
    });
});
builder.Services.AddTransient<ServiceNowRateLimitHandler>();
builder.Services.AddSingleton<IServiceNowTurnContextAccessor, ServiceNowTurnContextAccessor>();
builder.Services.AddSingleton<IServiceNowResultCache, ServiceNowResultCache>();
builder.Services.AddSingleton<IServiceNowOAuthTokenProvider, ServiceNowOAuthTokenProvider>();
builder.Services.AddSingleton<IServiceNowAiSearchClient, ServiceNowAiSearchClient>();
builder.AddAIAgent(hostedAgentName, (services, agentName) =>
{
    var foundry = services.GetRequiredService<IOptions<FoundryOptions>>().Value;
    var options = services.GetRequiredService<IOptions<AgentOptions>>().Value;
    var endpoint = new Uri(foundry.Endpoint);
    var openAiClient = new AzureOpenAIClient(endpoint, services.GetRequiredService<TokenCredential>());

    // Use Azure Responses to preserve reasoning during function-tool loops. Some deployments
    // reject tools combined with non-none reasoning on Chat Completions; this is model-dependent.
    // Azure.AI.OpenAI 2.9.0-beta.1 currently sends /openai/responses with
    // api-version=2025-04-01-preview, not the v1 endpoint exposed by our Development host.
    // Validate the target model/region with a live tool-call smoke test before rollout.
    ResponsesClient responsesBaseClient = openAiClient.GetResponsesClient();

    // The diagnostic decorator stays closest to the provider, below Agent Framework orchestration,
    // so every physical request in a tool loop is observed exactly once.
    IChatClient client = new ChatClientBuilder(responsesBaseClient.AsIChatClient(foundry.DeploymentName))
        .Use(innerClient => new DiagnosticChatClient(
            innerClient,
            services.GetRequiredService<IAiTurnTelemetryContext>(),
            services.GetRequiredService<IChatAiMetrics>(),
            AiComponents.MainAgent))
        .Build(services);

    // The employee's country is supplied in each prompt's [EMPLOYEE CONTEXT] section rather than
    // through a tool, so the model always has it before deciding how to search.
    var tools = new List<AITool>();

    if (AgentRuntimePolicy.EnablePayloadLogging(builder.Environment))
    {
        client = new ChatClientBuilder(client)
            .Use(innerClient => new PayloadLoggingChatClient(
                innerClient,
                services.GetRequiredService<DevelopmentPayloadWriter>()))
            .Build(services);
    }

    // Unified parallel retrieval tool — registered for all environments when enabled.
    // Supersedes the standalone ServiceNow tool; the model calls this once and receives a
    // merged, ranked result from both SharePoint and ServiceNow.
    if (sharePointSearchEnabled || serviceNowEnabled)
    {
        var unifiedTool = services.GetRequiredService<SearchHrKnowledgeTool>();
        tools.Add(AIFunctionFactory.Create(
            unifiedTool.SearchHrKnowledgeAsync,
            name: "search_hr_knowledge",
            description: "Searches approved HR policy and guidance from SharePoint and ServiceNow simultaneously. " +
                         "Returns a single merged and ranked list. Use two to four concise HR keywords including " +
                         "the country or region when relevant."));
    }

    // Invalid or unrepresentable efforts are rejected by options validation, never ignored.
    var reasoningOptions = foundry.CreateReasoningOptions();

    return client.AsAIAgent(
        new ChatClientAgentOptions
        {
            Name = agentName,
            ChatOptions = new ChatOptions
            {
                Instructions = options.Instructions,
                Tools = tools.Count > 0 ? tools : null,
                // The OpenAI conversation hosting adapter currently replays parallel function
                // calls as separate assistant messages before their tool results. Azure OpenAI
                // rejects that history on the next turn, so issue tool calls sequentially.
                AllowMultipleToolCalls = false,
                Reasoning = reasoningOptions
            }
        },
        loggerFactory: services.GetRequiredService<ILoggerFactory>(),
        services: services);
});
builder.Services.AddSingleton(services => services.GetRequiredKeyedService<AIAgent>(hostedAgentName));
builder.Services.AddSingleton(services =>
{
    var foundry = services.GetRequiredService<IOptions<FoundryOptions>>().Value;
    var endpoint = new Uri(foundry.Endpoint);
    var openAiClient = new AzureOpenAIClient(endpoint, services.GetRequiredService<TokenCredential>());
    return new IntentClassificationClient(new ChatClientBuilder(
        openAiClient.GetChatClient(foundry.DeploymentName).AsIChatClient())
        .Use(innerClient => new DiagnosticChatClient(
            innerClient,
            services.GetRequiredService<IAiTurnTelemetryContext>(),
            services.GetRequiredService<IChatAiMetrics>(),
            AiComponents.IntentClassifier))
        .Build(services));
});
builder.Services.AddSingleton<IAgentChatService, AgentChatService>();
if (configuredTeams.Enabled)
{
    builder.Services.AddSingleton<IStorage>(_ =>
        builder.Environment.IsDevelopment()
            ? new MemoryStorage()
            : new BlobsStorage(new Uri(configuredTeams.StorageContainerUri), azureCredential));
    builder.Services.AddDefaultMsalAuth(builder.Configuration);
    builder.Services.AddSingleton<TeamsSsoDiagnosticsMiddleware>();
    builder.AddAgent<HrTeamsAgent>();
}
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenAIResponses();
    builder.Services.AddOpenAIConversations();
}

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor(options => options.Credential = azureCredential);
}

var app = builder.Build();
if (configuredTeams.Enabled)
{
    // Temporary Teams SSO diagnostics: observe every Bot Connector turn ahead of AgentApplication.
    if (app.Services.GetRequiredService<IAgentHttpAdapter>() is IChannelAdapter channelAdapter)
    {
        channelAdapter.Use(app.Services.GetRequiredService<TeamsSsoDiagnosticsMiddleware>());
    }
    else
    {
        app.Logger.LogWarning("Teams SSO diagnostics middleware could not be registered on the Bot Connector adapter");
    }

    TeamsSsoConfigurationLogger.Log(app.Logger, app.Configuration, configuredTeams, configuredEntraAuthentication);
}

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
    app.MapOpenAIResponses();
    app.MapOpenAIConversations();
}
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapChatEndpoints();
if (configuredTeams.Enabled)
{
    app.MapPost("/api/messages", async (
        HttpRequest request,
        HttpResponse response,
        IAgentHttpAdapter adapter,
        IAgent agent,
        CancellationToken cancellationToken) =>
            await adapter.ProcessAsync(request, response, agent, cancellationToken))
        .RequireAuthorization(HrTeamsAgent.AuthorizationPolicyName)
        .ExcludeFromDescription();
}
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
app.MapGet("/", (IOptions<AgentOptions> options) => TypedResults.Ok(new { service = options.Value.Name, status = "running" }))
    .ExcludeFromDescription();
app.Run();

static bool IsHttpsUri(string value) =>
    Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

static ILogger GetBotConnectorLogger(HttpContext context) =>
    context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Orb.Automate.Agents.HR.Api.Auth.BotConnectorAuthentication");

static void ConfigureServiceNowHttpClient(IServiceProvider services, HttpClient client)
{
    _ = services.GetRequiredService<IOptions<ServiceNowOptions>>().Value;
    client.Timeout = Timeout.InfiniteTimeSpan;
}

static HttpRetryStrategyOptions CreateRetryOptions(int retries, int delayMilliseconds, string client) => new()
{
    MaxRetryAttempts = retries,
    Delay = TimeSpan.FromMilliseconds(delayMilliseconds),
    BackoffType = DelayBackoffType.Constant,
    UseJitter = true,
    ShouldHandle = CreateTransientPredicate(),
    OnRetry = _ =>
    {
        ServiceNowDiagnostics.RecordRetry(client);
        return default;
    }
};

static PredicateBuilder<HttpResponseMessage> CreateTransientPredicate() =>
    new PredicateBuilder<HttpResponseMessage>()
        .Handle<HttpRequestException>()
        .Handle<TimeoutRejectedException>()
        .HandleResult(static response =>
            response.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
            response.StatusCode == System.Net.HttpStatusCode.InternalServerError ||
            response.StatusCode == System.Net.HttpStatusCode.BadGateway ||
            response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
            response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout);

/// <summary>
/// Exposes the implicit program entry point for integration testing.
/// </summary>
public partial class Program;
