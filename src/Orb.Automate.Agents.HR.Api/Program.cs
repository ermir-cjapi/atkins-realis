using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Orb.Automate.Agents.HR.Api;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Endpoints;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.ServiceNow;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
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
    .ValidateOnStart();

builder.Services.AddSingleton(azureCredential);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DevelopmentPayloadWriter>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICurrentUserUpnAccessor, CurrentUserUpnAccessor>();
builder.Services.AddHttpClient(ServiceNowOAuthTokenProvider.HttpClientName, ConfigureServiceNowHttpClient)
    .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient(ServiceNowAiSearchClient.HttpClientName, ConfigureServiceNowHttpClient)
    .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler { AllowAutoRedirect = false });
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
    var options = services.GetRequiredService<IOptions<ServiceNowOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
}

/// <summary>
/// Exposes the implicit program entry point for integration testing.
/// </summary>
public partial class Program;
