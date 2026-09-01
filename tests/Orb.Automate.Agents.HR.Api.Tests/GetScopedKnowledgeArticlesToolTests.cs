using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the safe Agent Framework tool boundary.
/// </summary>
public sealed class GetScopedKnowledgeArticlesToolTests
{
    /// <summary>
    /// Missing trusted identity prevents an outbound search.
    /// </summary>
    [Fact]
    public async Task MissingIdentityDoesNotSearch()
    {
        var client = new StubSearchClient();
        var tool = CreateTool(null, client);

        var result = await tool.GetScopedKnowledgeArticlesAsync("leave", CancellationToken.None);

        Assert.Equal("identity_unavailable", result.Status);
        Assert.Equal(0, client.Calls);
    }

    /// <summary>
    /// Search results are returned without exposing the UPN as a tool parameter.
    /// </summary>
    [Fact]
    public async Task SuccessReturnsNormalizedResults()
    {
        var client = new StubSearchClient
        {
            Response = new ServiceNowSearchResponse(
                "leave",
                1,
                [new ServiceNowSearchResult("id", "kb_knowledge", "Title", "Body", [])])
        };
        var tool = CreateTool("person@example.com", client);

        var result = await tool.GetScopedKnowledgeArticlesAsync("leave", CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Equal("person@example.com", client.Upn);
        Assert.Single(result.Results);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.ReturnedCount);
        Assert.Equal("Body", result.Results[0].Content);
    }

    /// <summary>
    /// Results sent to the model are ranked, deduplicated and bounded by one global budget.
    /// </summary>
    [Fact]
    public void ModelResultIsRankedAndCompact()
    {
        var response = new ServiceNowSearchResponse(
            "Canada maternity leave",
            20,
            [
                new ServiceNowSearchResult(
                    "india-form",
                    "sc_cat_item",
                    "Leave Request - India",
                    "Maternity leave request",
                    [new ServiceNowSearchColumn("description", "Description", "India form", "India form")]),
                new ServiceNowSearchResult(
                    "canada-policy",
                    "kb_knowledge",
                    "Canada Maternity Leave Policy",
                    "Search snippet",
                    [
                        new ServiceNowSearchColumn("number", "Number", "KB001", "KB001"),
                        new ServiceNowSearchColumn("text", "Article body", new string('A', 3_000), new string('A', 3_000)),
                        new ServiceNowSearchColumn("kb_category", "Category", "internal-id", "Leave of Absence")
                    ]),
                new ServiceNowSearchResult("other", "kb_knowledge", "Unrelated policy", "Unrelated", [])
            ]);
        var settings = new ServiceNowOptions
        {
            MaxLlmResults = 2,
            MaxLlmResultContentCharacters = 2_500,
            MaxLlmPayloadCharacters = 1_000
        };

        var result = GetScopedKnowledgeArticlesTool.CreateModelResult(response, "Canada maternity leave", settings);
        var serialized = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(20, result.TotalCount);
        Assert.InRange(result.ReturnedCount, 1, 2);
        Assert.Equal("canada-policy", result.Results[0].SysId);
        Assert.Equal("KB001", result.Results[0].Number);
        Assert.Equal("Leave of Absence", result.Results[0].Category);
        Assert.DoesNotContain("Search snippet", result.Results[0].Content, StringComparison.Ordinal);
        Assert.True(serialized.Length <= settings.MaxLlmPayloadCharacters);
    }

    /// <summary>
    /// Controlled upstream failures produce a safe unavailable result.
    /// </summary>
    [Fact]
    public async Task ServiceNowFailureReturnsUnavailable()
    {
        var client = new StubSearchClient
        {
            Exception = new ServiceNowException(ServiceNowFailureKind.Throttled, "throttled")
        };
        var tool = CreateTool("person@example.com", client);

        var result = await tool.GetScopedKnowledgeArticlesAsync("leave", CancellationToken.None);

        Assert.Equal("unavailable", result.Status);
        Assert.Empty(result.Results);
    }

    private static GetScopedKnowledgeArticlesTool CreateTool(string? upn, IServiceNowAiSearchClient client) =>
        new(
            new StubUpnAccessor(upn),
            client,
            Microsoft.Extensions.Options.Options.Create(new ServiceNowOptions()),
            new DevelopmentPayloadWriter(new NonDevelopmentEnvironment()),
            NullLogger<GetScopedKnowledgeArticlesTool>.Instance);

    private sealed class NonDevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubUpnAccessor(string? upn) : ICurrentUserUpnAccessor
    {
        public string? GetCurrentUserUpn() => upn;
    }

    private sealed class StubSearchClient : IServiceNowAiSearchClient
    {
        public int Calls { get; private set; }

        public string? Upn { get; private set; }

        public ServiceNowSearchResponse Response { get; init; } =
            new("leave", 0, []);

        public Exception? Exception { get; init; }

        public Task<ServiceNowSearchResponse> SearchAsync(
            string upn,
            string query,
            CancellationToken cancellationToken)
        {
            Calls++;
            Upn = upn;
            return Exception is null
                ? Task.FromResult(Response)
                : Task.FromException<ServiceNowSearchResponse>(Exception);
        }
    }
}
