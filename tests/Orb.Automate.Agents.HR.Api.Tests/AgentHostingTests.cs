using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Diagnostics;

namespace Orb.Automate.Agents.HR.Api.Tests;

#pragma warning disable OPENAI001 // Offline request-contract tests exercise the experimental Responses SDK.

/// <summary>
/// Tests agent registration and production capability boundaries.
/// </summary>
public sealed class AgentHostingTests
{
    /// <summary>
    /// Verifies the hosted agent can be constructed without either retrieval integration.
    /// </summary>
    [Fact]
    public void AgentResolvesWithBothIntegrationsDisabled()
    {
        using var application = new AgentApplication(sharePointEnabled: false);

        Assert.NotNull(application.Services.GetRequiredService<AIAgent>());
        Assert.NotNull(application.Services.GetRequiredService<IAgentChatService>());
        Assert.NotNull(application.Services.GetRequiredService<IAiTurnTelemetryContext>());
    }

    /// <summary>
    /// Verifies SharePoint registration does not introduce a ServiceNow dependency.
    /// </summary>
    [Fact]
    public void AgentResolvesWithSharePointEnabledAndServiceNowDisabled()
    {
        using var application = new AgentApplication(sharePointEnabled: true);

        Assert.NotNull(application.Services.GetRequiredService<AIAgent>());
        Assert.NotNull(application.Services.GetRequiredService<SearchHrPolicyDocumentsTool>());
        Assert.NotNull(application.Services.GetRequiredService<IAgentChatService>());
    }

    /// <summary>
    /// Verifies sensitive payload diagnostics and model-controlled SharePoint retrieval remain
    /// disabled outside Development.
    /// </summary>
    [Fact]
    public void ProductionDisablesDevelopmentOnlyAgentCapabilities()
    {
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Production };
        using var application = new AgentApplication(sharePointEnabled: true);

        Assert.False(AgentRuntimePolicy.EnablePayloadLogging(environment));
        Assert.False(AgentRuntimePolicy.EnableModelControlledSharePointRetrieval(environment));
        Assert.Null(application.Services.GetService<DevelopmentPayloadWriter>());
    }

    /// <summary>
    /// Verifies the agent resolves when <c>Foundry:ReasoningEffort</c> is not set (backward
    /// compatibility — non-reasoning models do not need the setting).
    /// </summary>
    [Fact]
    public void AgentResolvesWhenReasoningEffortIsOmitted()
    {
        using var application = new AgentApplication(sharePointEnabled: false, reasoningEffort: null);

        Assert.NotNull(application.Services.GetRequiredService<AIAgent>());
    }

    /// <summary>
    /// Verifies the agent resolves for every valid <see cref="ReasoningEffort"/> level so that
    /// swapping configuration does not break startup. Provider support remains model-dependent;
    /// the request-contract tests below check serialization separately.
    /// </summary>
    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("extrahigh")]
    [InlineData("none")]
    [InlineData("Low")]      // case-insensitive
    [InlineData("HIGH")]     // case-insensitive
    public void AgentResolvesWithValidReasoningEffort(string reasoningEffort)
    {
        using var application = new AgentApplication(sharePointEnabled: false, reasoningEffort: reasoningEffort);

        Assert.NotNull(application.Services.GetRequiredService<AIAgent>());
    }

    /// <summary>
    /// Verifies invalid names and numeric enum values fail startup rather than silently using
    /// the model's default reasoning effort.
    /// </summary>
    [Theory]
    [InlineData("ultramax")]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("max")]
    public void AgentRejectsInvalidReasoningEffortAtStartup(string reasoningEffort)
    {
        using var application = new AgentApplication(sharePointEnabled: false, reasoningEffort: reasoningEffort);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            application.Services.GetRequiredService<AIAgent>());
        Assert.Contains(exception.Failures, failure => failure.Contains("ReasoningEffort", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies real startup options reach the SDK request; merely constructing an agent cannot
    /// establish that its configured reasoning effort is forwarded.
    /// </summary>
    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("extrahigh", "xhigh")]
    [InlineData("Low", "low")]
    [InlineData("HIGH", "high")]
    [InlineData("none", "none")]
    [InlineData("xhigh", "xhigh")]
    [InlineData(" XHIGH ", "xhigh")]
    public async Task ConfiguredReasoningEffortIsSerialized(string configured, string expected)
    {
        using var application = new AgentApplication(sharePointEnabled: false, reasoningEffort: configured);
        var options = GetRegisteredChatOptions(application);

        var request = await CaptureResponsesRequestAsync(options);

        Assert.Equal(expected, request.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    /// <summary>
    /// Verifies both Responses failure shapes (a mid-stream <c>error</c> event and a terminal
    /// <c>response.failed</c> event, which carries no error content) become an exception and failed
    /// telemetry through the SDK, diagnostic decorator, and Agent Framework pipeline.
    /// </summary>
    [Theory]
    [InlineData(ErrorEventStream)]
    [InlineData(FailedResponseStream)]
    public async Task ResponsesWireFailureFailsAgentAndProviderTelemetry(string sse)
    {
        var (metrics, exception, _) = await RunWireStreamAsync(sse);

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(ProviderCallOutcome.Failed, Assert.Single(metrics.ProviderCalls).Outcome);
    }

    /// <summary>
    /// Verifies a model refusal, which the adapter reports as refusal error content, is treated as a
    /// completed answer rather than a provider failure.
    /// </summary>
    [Fact]
    public async Task ResponsesWireRefusalIsNotAProviderFailure()
    {
        var (metrics, exception, requests) = await RunWireStreamAsync(RefusalStream);

        Assert.Null(exception);
        Assert.Equal(ProviderCallOutcome.Succeeded, Assert.Single(metrics.ProviderCalls).Outcome);
        Assert.True(Assert.Single(requests).Body.GetProperty("stream").GetBoolean());
    }

    private static async Task<(RecordingChatAiMetrics Metrics, Exception? Exception, List<(Uri Uri, JsonElement Body)> Requests)>
        RunWireStreamAsync(string sse)
    {
        using var handler = new RecordingResponsesHandler { StreamBody = sse };
        using var httpClient = new HttpClient(handler);
        var azureClient = CreateOfflineAzureClient(httpClient);
        using var inner = azureClient.GetResponsesClient().AsIChatClient("test-deployment");
        var context = new AiTurnTelemetryContext();
        var metrics = new RecordingChatAiMetrics();
        using var turn = context.BeginTurn();
        using var client = new DiagnosticChatClient(inner, context, metrics, AiComponents.MainAgent);
        var agent = client.AsAIAgent();

        var exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("Offline test"))
            {
            }
        });

        return (metrics, exception, handler.Requests);
    }

    private const string ResponseFields =
        "\"id\":\"resp_offline\",\"object\":\"response\",\"created_at\":1,\"model\":\"test-deployment\",\"output\":[]";

    private const string ErrorEventStream =
        "event: error\ndata: {\"type\":\"error\",\"sequence_number\":1,\"code\":\"server_error\",\"message\":\"Simulated wire failure\"}\n\n";

    private const string FailedResponseStream =
        "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{" + ResponseFields + ",\"status\":\"in_progress\"}}\n\n" +
        "event: response.failed\ndata: {\"type\":\"response.failed\",\"sequence_number\":1,\"response\":{" + ResponseFields +
        ",\"status\":\"failed\",\"error\":{\"code\":\"server_error\",\"message\":\"Simulated failure\"}}}\n\n";

    private const string RefusalStream =
        "event: response.refusal.done\ndata: {\"type\":\"response.refusal.done\",\"sequence_number\":0,\"item_id\":\"msg_offline\",\"output_index\":0,\"content_index\":0,\"refusal\":\"I can't help with that.\"}\n\n" +
        "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":1,\"response\":{" + ResponseFields + ",\"status\":\"completed\"}}\n\n";

    /// <summary>Verifies a simulated function call round trip using real startup options and SDK serialization.</summary>
    [Fact]
    public async Task ResponsesToolLoopSerializesToolOutputAndProducesFinalAnswer()
    {
        using var application = new AgentApplication(sharePointEnabled: false, reasoningEffort: "low");
        var options = GetRegisteredChatOptions(application);
        var toolCalls = 0;
        options.Tools = [AIFunctionFactory.Create(() =>
        {
            toolCalls++;
            return "Approved offline guidance.";
        }, "offline_tool")];
        using var handler = new RecordingResponsesHandler { ToolLoop = true };
        using var httpClient = new HttpClient(handler);
        using var client = CreateOfflineAzureClient(httpClient).GetResponsesClient().AsIChatClient("test-deployment");
        var agent = client.AsAIAgent(new ChatClientAgentOptions { ChatOptions = options });

        var response = await agent.RunAsync("Offline tool-loop test.");

        Assert.Equal("OK", response.Text);
        Assert.Equal(1, toolCalls);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("offline_tool", Assert.Single(handler.Requests[0].Body.GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
        Assert.False(handler.Requests[0].Body.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.All(handler.Requests, request =>
            Assert.Equal("low", request.Body.GetProperty("reasoning").GetProperty("effort").GetString()));
        var toolOutput = Assert.Single(handler.Requests[1].Body.GetProperty("input").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "function_call_output");
        Assert.Equal("call_offline", toolOutput.GetProperty("call_id").GetString());
        // Agent Framework encodes function return values as JSON inside the output string.
        Assert.Equal("Approved offline guidance.",
            JsonSerializer.Deserialize<string>(toolOutput.GetProperty("output").GetString()!));
    }

    /// <summary>Verifies omitted or blank effort leaves the provider's reasoning default intact.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnconfiguredReasoningEffortIsAbsentFromRequest(string? configured)
    {
        using var application = new AgentApplication(sharePointEnabled: false, reasoningEffort: configured);

        var request = await CaptureResponsesRequestAsync(GetRegisteredChatOptions(application));

        Assert.False(request.TryGetProperty("reasoning", out _));
    }

    /// <summary>
    /// Defines a no-provider-retention contract for HR payloads: storage must be explicitly
    /// disabled rather than delegated to the Responses API's retention default.
    /// </summary>
    [Fact(Skip = "Pending HR response-retention policy decision; enable once storage is configured explicitly.")]
    public async Task HrResponsesRequestExplicitlyDisablesStorage()
    {
        using var application = new AgentApplication(sharePointEnabled: false);

        var request = await CaptureResponsesRequestAsync(GetRegisteredChatOptions(application));

        Assert.True(request.TryGetProperty("store", out var store),
            "HR requests must explicitly set store=false, not inherit provider retention defaults.");
        Assert.Equal(JsonValueKind.False, store.ValueKind);
    }

    private static ChatOptions GetRegisteredChatOptions(AgentApplication application)
    {
        var agent = application.Services.GetRequiredService<AIAgent>();
        return Assert.IsType<ChatOptions>(agent.GetService(typeof(ChatOptions))).Clone();
    }

    private static async Task<JsonElement> CaptureResponsesRequestAsync(ChatOptions options)
    {
        // Reuse the production agent's options, not a duplicate of its parsing/configuration.
        // Only the provider transport is substituted; no Azure endpoint or credential is used.
        using var handler = new RecordingResponsesHandler();
        using var httpClient = new HttpClient(handler);
        var azureClient = CreateOfflineAzureClient(httpClient);
        using var client = azureClient.GetResponsesClient().AsIChatClient("test-deployment");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Offline request-contract test.")], options);

        Assert.Equal("OK", response.Text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/openai/responses", request.Uri.AbsolutePath);
        Assert.Equal("?api-version=2025-04-01-preview", request.Uri.Query);
        Assert.Equal("test-deployment", request.Body.GetProperty("model").GetString());
        return request.Body;
    }

    private static AzureOpenAIClient CreateOfflineAzureClient(HttpClient httpClient) => new(
        new Uri("https://example.openai.azure.com/"),
        new ApiKeyCredential("offline-test-not-a-real-key"),
        new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(httpClient) });

    private sealed class RecordingResponsesHandler : HttpMessageHandler
    {
        public List<(Uri Uri, JsonElement Body)> Requests { get; } = [];

        public string? StreamBody { get; init; }

        public bool ToolLoop { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add((request.RequestUri!, body.RootElement.Clone()));
            if (StreamBody is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(StreamBody, Encoding.UTF8, "text/event-stream")
                };
            }

            var responseBody = ToolLoop && Requests.Count == 1
                ? """
                        {
                          "id": "resp_tool", "object": "response", "created_at": 1,
                          "status": "completed", "model": "test-deployment", "store": false,
                          "output": [{ "id": "fc_offline", "type": "function_call",
                            "call_id": "call_offline", "name": "offline_tool", "arguments": "{}", "status": "completed" }]
                        }
                        """
                    : """
                    {
                      "id": "resp_offline", "object": "response", "created_at": 1,
                      "status": "completed", "model": "test-deployment",
                      "output": [{
                        "id": "msg_offline", "type": "message", "role": "assistant", "status": "completed",
                        "content": [{ "type": "output_text", "text": "OK", "annotations": [] }]
                      }],
                      "usage": { "input_tokens": 1, "output_tokens": 1, "total_tokens": 2 }
                    }
                    """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class AgentApplication(bool sharePointEnabled, string? reasoningEffort = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.UseSetting("SharePointSearch:Enabled", sharePointEnabled.ToString());
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Agent:Name"] = "Test Agent",
                    ["Foundry:Endpoint"] = "https://example.openai.azure.com/",
                    ["Foundry:DeploymentName"] = "test-deployment",
                    ["EntraAuthentication:TenantId"] = "00000000-0000-0000-0000-000000000000",
                    ["EntraAuthentication:Audience"] = "api://test-agent",
                    ["EntraAuthentication:RequiredScope"] = "access_as_user",
                    ["SharePointSearch:Endpoint"] = "https://example.search.windows.net/",
                    ["SharePointSearch:IndexName"] = "test-index",
                    ["SharePointSearch:SemanticConfigurationName"] = "test-semantic-configuration",
                    ["OnBehalfOf:TenantId"] = "00000000-0000-0000-0000-000000000000",
                    ["OnBehalfOf:ClientId"] = "00000000-0000-0000-0000-000000000001",
                    ["OnBehalfOf:UserTokenMode"] = "OnBehalfOf",
                    ["ServiceNow:Enabled"] = "false",
                    ["Foundry:ReasoningEffort"] = reasoningEffort
                };

                configuration.AddInMemoryCollection(settings);
            });
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}