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
                    ["Foundry:DeploymentName"] = "test-deployment"
                }));
        }
    }
}
