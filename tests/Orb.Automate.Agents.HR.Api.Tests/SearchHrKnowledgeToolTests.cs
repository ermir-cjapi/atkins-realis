using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.Search;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the SharePoint token handling of the unified <see cref="SearchHrKnowledgeTool"/>.
/// </summary>
public sealed class SearchHrKnowledgeToolTests
{
    /// <summary>
    /// Verifies that no on-behalf-of token is requested when permission trimming is disabled,
    /// so a missing delegated consent cannot block SharePoint search.
    /// </summary>
    [Fact]
    public async Task SearchHrKnowledgeAsync_SkipsUserTokenWhenPermissionsAreNotEnforced()
    {
        var tokenProvider = new CountingTokenProvider(token: null);
        var searchClient = new RecordingSearchClient();
        var tool = CreateTool(tokenProvider, searchClient, enforceDocumentPermissions: false);

        await tool.SearchHrKnowledgeAsync("maternity leave", CancellationToken.None);

        Assert.Equal(0, tokenProvider.Calls);
        Assert.True(searchClient.Called);
        Assert.Null(searchClient.UserToken);
    }

    /// <summary>
    /// Verifies that permission-trimmed search fails closed when no delegated token is available.
    /// </summary>
    [Fact]
    public async Task SearchHrKnowledgeAsync_DoesNotSearchWhenEnforcedTokenIsMissing()
    {
        var tokenProvider = new CountingTokenProvider(token: null);
        var searchClient = new RecordingSearchClient();
        var tool = CreateTool(tokenProvider, searchClient, enforceDocumentPermissions: true);

        await tool.SearchHrKnowledgeAsync("maternity leave", CancellationToken.None);

        Assert.Equal(1, tokenProvider.Calls);
        Assert.False(searchClient.Called);
    }

    /// <summary>
    /// Verifies that the delegated token is forwarded when permission trimming is enforced.
    /// </summary>
    [Fact]
    public async Task SearchHrKnowledgeAsync_ForwardsUserTokenWhenPermissionsAreEnforced()
    {
        var tokenProvider = new CountingTokenProvider(token: "user-token");
        var searchClient = new RecordingSearchClient();
        var tool = CreateTool(tokenProvider, searchClient, enforceDocumentPermissions: true);

        await tool.SearchHrKnowledgeAsync("maternity leave", CancellationToken.None);

        Assert.Equal(1, tokenProvider.Calls);
        Assert.Equal("user-token", searchClient.UserToken);
    }

    private static SearchHrKnowledgeTool CreateTool(
        ISearchUserTokenProvider tokenProvider,
        ISharePointSearchClient searchClient,
        bool enforceDocumentPermissions) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new SharePointSearchOptions
            {
                EnforceDocumentPermissions = enforceDocumentPermissions,
            }),
            new GroundingTelemetryContext(new RecordingChatAiMetrics()),
            NullLogger<SearchHrKnowledgeTool>.Instance,
            tokenProvider: tokenProvider,
            spClient: searchClient);

    private sealed class CountingTokenProvider(string? token) : ISearchUserTokenProvider
    {
        public int Calls { get; private set; }

        public Task<string?> GetTokenAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(token);
        }
    }

    private sealed class RecordingSearchClient : ISharePointSearchClient
    {
        public bool Called { get; private set; }

        public string? UserToken { get; private set; }

        public Task<SharePointSearchResponse> SearchAsync(
            string term,
            string? userToken,
            CancellationToken cancellationToken)
        {
            Called = true;
            UserToken = userToken;
            return Task.FromResult(new SharePointSearchResponse(term, []));
        }
    }
}
