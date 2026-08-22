using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Orb.Automate.Agents.HR.Api;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Endpoints;
using Orb.Automate.Agents.HR.Api.Options;
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

builder.Services.AddSingleton(azureCredential);
builder.AddAIAgent(hostedAgentName, (services, agentName) =>
{
    var foundry = services.GetRequiredService<IOptions<FoundryOptions>>().Value;
    var options = services.GetRequiredService<IOptions<AgentOptions>>().Value;
    var endpoint = new Uri(foundry.Endpoint);
    var openAiClient = new AzureOpenAIClient(endpoint, services.GetRequiredService<TokenCredential>());
    ChatClient client = openAiClient
        .GetChatClient(foundry.DeploymentName);

    return client.AsAIAgent(
        name: agentName,
        instructions: options.Instructions,
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

/// <summary>
/// Exposes the implicit program entry point for integration testing.
/// </summary>
public partial class Program;
