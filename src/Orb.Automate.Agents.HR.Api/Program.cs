using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Orb.Automate.Agents.HR.Api;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Endpoints;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.ServiceNow;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Scalar.AspNetCore;
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
        options.SearchAttemptTimeoutMilliseconds < options.TurnBudgetSeconds * 1_000 &&
        options.OAuthAttemptTimeoutMilliseconds < options.TurnBudgetSeconds * 1_000,
        "ServiceNow per-attempt timeouts must be shorter than TurnBudgetSeconds.")
    .Validate(static options =>
        options.CircuitSamplingSeconds * 1_000 >= options.SearchAttemptTimeoutMilliseconds * 2,
        "ServiceNow:CircuitSamplingSeconds must be at least twice SearchAttemptTimeoutMilliseconds.")
    .Validate(static options =>
        options.RateLimitRequestsPerSecond <= options.RateLimitBurst,
        "ServiceNow:RateLimitRequestsPerSecond must not exceed RateLimitBurst.")
    .ValidateOnStart();

builder.Services.AddSingleton(azureCredential);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DevelopmentPayloadWriter>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICurrentUserUpnAccessor, CurrentUserUpnAccessor>();
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
                SamplingDuration = TimeSpan.FromSeconds(settings.CircuitSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(settings.CircuitBreakSeconds),
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
builder.Services.AddSingleton<GetScopedKnowledgeArticlesTool>();
builder.AddAIAgent(hostedAgentName, (services, agentName) =>
{
    var foundry = services.GetRequiredService<IOptions<FoundryOptions>>().Value;
    var options = services.GetRequiredService<IOptions<AgentOptions>>().Value;
    var endpoint = new Uri(foundry.Endpoint);
    var openAiClient = new AzureOpenAIClient(endpoint, services.GetRequiredService<TokenCredential>());
    ChatClient baseClient = openAiClient.GetChatClient(foundry.DeploymentName);
    var client = new ChatClientBuilder(baseClient.AsIChatClient())
        .Use(innerClient => new PayloadLoggingChatClient(
            innerClient,
            services.GetRequiredService<DevelopmentPayloadWriter>()))
        .Build(services);
    var serviceNowTool = services.GetRequiredService<GetScopedKnowledgeArticlesTool>();

    return client.AsAIAgent(
        name: agentName,
        instructions: options.Instructions,
        tools:
        [
            AIFunctionFactory.Create(
                serviceNowTool.GetScopedKnowledgeArticlesAsync,
                name: "get_scoped_knowledge_articles",
                description: "Searches ServiceNow HR knowledge and catalogue content using two to four concise keywords, including the country or region when relevant, and the verified current-user identity.")
        ],
        loggerFactory: services.GetRequiredService<ILoggerFactory>(),
        services: services);
});
builder.Services.AddSingleton(services => services.GetRequiredKeyedService<AIAgent>(hostedAgentName));
builder.Services.AddSingleton<IAgentChatService, AgentChatService>();
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
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
    app.MapOpenAIResponses();
    app.MapOpenAIConversations();
}
app.UseHttpsRedirection();

app.MapChatEndpoints();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");
app.MapGet("/", (IOptions<AgentOptions> options) => TypedResults.Ok(new { service = options.Value.Name, status = "running" }))
    .ExcludeFromDescription();
app.Run();

static bool IsHttpsUri(string value) =>
    Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

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
