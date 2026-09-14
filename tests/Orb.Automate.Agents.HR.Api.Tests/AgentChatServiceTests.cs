using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.Search;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the citation policy shared by complete and streamed grounded responses.
/// </summary>
public sealed class AgentChatServiceTests
{
    private const string ApprovedUrl = "https://contoso.sharepoint.com/sites/hr/policy.aspx";

    /// <summary>
    /// Verifies grounded stream content is replaced before release when it omits an approved citation.
    /// </summary>
    [Fact]
    public void StreamingCitationPolicyRejectsUncitedGroundedResponse()
    {
        var (responseText, citations) = AgentChatService.ApplyCitationPolicy(
            "Employees receive annual leave.",
            CreateGrounding());

        Assert.Equal(
            "I don't have sufficient approved source material to answer that safely. Please raise an HR Case.",
            responseText);
        Assert.Empty(citations);
    }

    /// <summary>
    /// Verifies grounded stream content containing an exact ledger URL passes validation.
    /// </summary>
    [Fact]
    public void StreamingCitationPolicyAcceptsExactApprovedCitation()
    {
        var response = $"Employees receive annual leave. See {ApprovedUrl}.";

        var (responseText, citations) = AgentChatService.ApplyCitationPolicy(response, CreateGrounding());

        Assert.Equal(response, responseText);
        var citation = Assert.Single(citations);
        Assert.Equal(ApprovedUrl, citation.Url);
        Assert.Equal("chunk-1", citation.RetrievalId);
    }

    /// <summary>
    /// Verifies the streaming service emits only the safe fallback when the completed grounded
    /// response does not contain an approved citation.
    /// </summary>
    [Fact]
    public async Task StreamResponseWithholdsUncitedGroundedChunks()
    {
        using var chatClient = new StubChatClient("Employees receive ", "annual leave.");
        using var services = CreateServices();
        var service = new AgentChatService(
            chatClient.AsAIAgent(),
            services,
            new StubTurnContextAccessor(),
            NullLogger<AgentChatService>.Instance);
        var updates = new List<Contracts.ChatStreamUpdate>();

        await foreach (var update in service.StreamResponseAsync("annual leave", CancellationToken.None))
        {
            updates.Add(update);
        }

        var finalUpdate = Assert.Single(updates);
        Assert.Equal(
            "I don't have sufficient approved source material to answer that safely. Please raise an HR Case.",
            finalUpdate.Text);
    }

    private static SearchHrPolicyDocumentsResult CreateGrounding() =>
        new(
            "success",
            "Search succeeded.",
            "annual leave",
            1,
            1,
            [
                new HrPolicyDocumentResult(
                    "chunk-1",
                    "document-1",
                    "Annual Leave",
                    "Approved annual leave guidance.",
                    ApprovedUrl,
                    3.5,
                    "1",
                    null,
                    "UK",
                    "UK",
                    "Employees")
            ]);

    private static ServiceProvider CreateServices()
    {
        var tool = new SearchHrPolicyDocumentsTool(
            new StubTokenProvider(),
            new StubSearchClient(),
            Microsoft.Extensions.Options.Options.Create(new SharePointSearchOptions()),
            NullLogger<SearchHrPolicyDocumentsTool>.Instance);
        return new ServiceCollection()
            .AddSingleton(tool)
            .BuildServiceProvider();
    }

    private sealed class StubTokenProvider : ISearchUserTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-token");
    }

    private sealed class StubSearchClient : ISharePointSearchClient
    {
        public Task<SharePointSearchResponse> SearchAsync(
            string term,
            string userToken,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SharePointSearchResponse(
                term,
                [
                    new SharePointSearchResult(
                        "chunk-1",
                        "document-1",
                        "Annual Leave",
                        "Approved annual leave guidance.",
                        ApprovedUrl,
                        3.5)
                ]));
    }

    private sealed class StubChatClient(params string[] updates) : IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var text in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubTurnContextAccessor : IServiceNowTurnContextAccessor
    {
        public IDisposable BeginTurn() => NullDisposable.Instance;

        public ServiceNowTurnContext? Current => null;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
