using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests health check endpoints exposed by the API.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<HealthEndpointTests.ChatbotApplication>
{
    private readonly HttpClient _client;

    /// <summary>
    /// Creates a new test fixture using the supplied application host.
    /// </summary>
    /// <param name="application">The test application host.</param>
    public HealthEndpointTests(ChatbotApplication application)
    {
        _client = application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    /// <summary>
    /// Verifies the live health endpoint returns a healthy status.
    /// </summary>
    [Fact]
    public async Task LiveReturnsHealthy()
    {
        var response = await _client.GetAsync(new Uri("/health/live", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Verifies the root endpoint returns the configured agent name.
    /// </summary>
    [Fact]
    public async Task RootReturnsConfiguredAgentName()
    {
        var response = await _client.GetAsync(new Uri("/", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Test Agent", payload.GetProperty("service").GetString());
        Assert.Equal("running", payload.GetProperty("status").GetString());
    }

    /// <summary>
    /// Verifies chat cannot be invoked without a validated Entra access token.
    /// </summary>
    [Fact]
    public async Task ChatRejectsUnauthenticatedRequests()
    {
        var response = await _client.PostAsJsonAsync(
            new Uri("/api/chat/", UriKind.Relative),
            new { message = "How does sick leave work?" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Verifies the development-only agent endpoints are not exposed in other environments.
    /// </summary>
    [Fact]
    public async Task AgentEndpointsAreNotExposedOutsideDevelopment()
    {
        var response = await _client.GetAsync(new Uri("/v1/responses", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Test application factory used to configure the API host for integration tests.
    /// </summary>
    public sealed class ChatbotApplication : WebApplicationFactory<Program>
    {
        /// <summary>
        /// Configures the test web host with the minimal settings required by the API.
        /// </summary>
        /// <param name="builder">The web host builder.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Agent:Name"] = "Test Agent",
                    ["Foundry:Endpoint"] = "https://example.openai.azure.com/",
                    ["Foundry:DeploymentName"] = "test-deployment",
                    ["EntraAuthentication:TenantId"] = "00000000-0000-0000-0000-000000000000",
                    ["EntraAuthentication:Audience"] = "api://test-agent",
                    ["EntraAuthentication:RequiredScope"] = "access_as_user"
                }));
        }
    }
}

/// <summary>
/// Tests the OpenAI-compatible agent surface consumed by the Aspire DevUI aggregator.
/// </summary>
public sealed class AgentEndpointTests : IClassFixture<AgentEndpointTests.DevelopmentApplication>
{
    private readonly HttpClient _client;

    /// <summary>
    /// Creates a new fixture using a Development application host.
    /// </summary>
    /// <param name="application">The development application host.</param>
    public AgentEndpointTests(DevelopmentApplication application)
    {
        _client = application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    /// <summary>
    /// Verifies the OpenAI Responses route is mapped in Development.
    /// </summary>
    [Fact]
    public async Task ResponsesRouteIsMappedInDevelopment()
    {
        var response = await _client.GetAsync(new Uri("/v1/responses", UriKind.Relative));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    /// <summary>
    /// Verifies the OpenAI Conversations route is mapped in Development.
    /// </summary>
    [Fact]
    public async Task ConversationsRouteIsMappedInDevelopment()
    {
        var response = await _client.GetAsync(new Uri("/v1/conversations", UriKind.Relative));

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Development application factory with the minimum required API settings.
    /// </summary>
    public sealed class DevelopmentApplication : WebApplicationFactory<Program>
    {
        /// <summary>
        /// Configures the API host as Development so the OpenAI routes are registered and mapped.
        /// </summary>
        /// <param name="builder">The web host builder.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Agent:Name"] = "Test Agent",
                    ["Foundry:Endpoint"] = "https://example.openai.azure.com/",
                    ["Foundry:DeploymentName"] = "test-deployment",
                    ["EntraAuthentication:TenantId"] = "00000000-0000-0000-0000-000000000000",
                    ["EntraAuthentication:Audience"] = "api://test-agent",
                    ["EntraAuthentication:RequiredScope"] = "access_as_user"
                }));
        }
    }
}
