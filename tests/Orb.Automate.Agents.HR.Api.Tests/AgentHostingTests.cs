using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Diagnostics;

namespace Orb.Automate.Agents.HR.Api.Tests;

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

    private sealed class AgentApplication(bool sharePointEnabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.UseSetting("SharePointSearch:Enabled", sharePointEnabled.ToString());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
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
                    ["ServiceNow:Enabled"] = "false"
                }));
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