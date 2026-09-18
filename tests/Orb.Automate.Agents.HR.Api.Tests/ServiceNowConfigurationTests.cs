using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Verifies startup validation and named ServiceNow HTTP pipeline isolation.
/// </summary>
public sealed class ServiceNowConfigurationTests
{
    /// <summary>
    /// Invalid timeout, cache, and rate hierarchies fail startup validation.
    /// </summary>
    [Theory]
    [InlineData("ServiceNow:SearchAttemptTimeoutMilliseconds", "10000")]
    [InlineData("ServiceNow:CacheFreshMinutes", "61")]
    [InlineData("ServiceNow:RateLimitRequestsPerSecond", "6")]
    public void InvalidBoundedSettingsFailStartup(string key, string value)
    {
        using var application = new ConfiguredApplication(new Dictionary<string, string?> { [key] = value });

        Assert.ThrowsAny<Exception>(() => application.CreateClient());
    }

    /// <summary>
    /// The named search client performs only its configured single retry rather than stacking global resilience.
    /// </summary>
    [Fact]
    public async Task SearchClientDoesNotStackGlobalResiliencePipeline()
    {
        var calls = 0;
        using var application = new ConfiguredApplication(
            configureServices: services => services
                .AddHttpClient(ServiceNowAiSearchClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new DelegateHttpMessageHandler((_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                })));
        using var client = application.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ServiceNowAiSearchClient.HttpClientName);

        using var response = await client.GetAsync(new Uri("https://example.service-now.com/search"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, calls);
    }

    private sealed class ConfiguredApplication : WebApplicationFactory<Program>
    {
        private readonly IReadOnlyDictionary<string, string?> _settings;
        private readonly Action<IServiceCollection>? _configureServices;

        public ConfiguredApplication(
            IReadOnlyDictionary<string, string?>? settings = null,
            Action<IServiceCollection>? configureServices = null)
        {
            _settings = settings ?? new Dictionary<string, string?>();
            _configureServices = configureServices;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Agent:Name"] = "Test Agent",
                    ["Foundry:Endpoint"] = "https://example.openai.azure.com/",
                    ["Foundry:DeploymentName"] = "test-deployment",
                    ["EntraAuthentication:TenantId"] = "00000000-0000-0000-0000-000000000000",
                    ["EntraAuthentication:Audience"] = "api://test-agent",
                    ["EntraAuthentication:RequiredScope"] = "access_as_user"
                };
                foreach (var setting in _settings)
                {
                    values[setting.Key] = setting.Value;
                }

                configuration.AddInMemoryCollection(values);
            });
            if (_configureServices is not null)
            {
                builder.ConfigureServices(_configureServices);
            }
        }
    }
}
