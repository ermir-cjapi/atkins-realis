using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.Search;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the agent-facing projection applied to SharePoint knowledge results.
/// </summary>
public sealed class SearchHrPolicyDocumentsToolTests
{
    /// <summary>
    /// Verifies that results below the reranker floor are not offered to the agent.
    /// </summary>
    [Fact]
    public void CreateModelResult_DropsResultsBelowTheRerankerFloor()
    {
        var response = new SharePointSearchResponse("sickness absence", [CreateResult("c1", "doc-1", 1.0d)]);

        var result = SearchHrPolicyDocumentsTool.CreateModelResult(response, CreateSettings());

        Assert.Equal("no_results", result.Status);
        Assert.Equal(1, result.RetrievedCount);
        Assert.Equal(0, result.ReturnedCount);
        Assert.Empty(result.Results);
    }

    /// <summary>
    /// Verifies that a result without a citation URL is discarded, because it cannot be cited.
    /// </summary>
    [Fact]
    public void CreateModelResult_DropsResultsWithoutACitationUrl()
    {
        var response = new SharePointSearchResponse(
            "sickness absence",
            [CreateResult("c1", "doc-1", 3.4d, url: string.Empty)]);

        var result = SearchHrPolicyDocumentsTool.CreateModelResult(response, CreateSettings());

        Assert.Equal("no_results", result.Status);
        Assert.Empty(result.Results);
    }

    /// <summary>
    /// Verifies that complementary chunks from one document are retained within the configured cap.
    /// </summary>
    [Fact]
    public void CreateModelResult_KeepsComplementaryChunksPerDocument()
    {
        var response = new SharePointSearchResponse(
            "sickness absence",
            [
                CreateResult("c1", "doc-1", 2.0d),
                CreateResult("c2", "doc-1", 3.5d),
                CreateResult("c3", "doc-2", 2.5d),
            ]);

        var result = SearchHrPolicyDocumentsTool.CreateModelResult(response, CreateSettings());

        Assert.Equal("success", result.Status);
        Assert.Equal(3, result.RetrievedCount);
        Assert.Equal(3, result.ReturnedCount);
        Assert.Equal(3.5d, result.Results[0].Score);
        Assert.Equal(2.5d, result.Results[1].Score);
    }

    /// <summary>
    /// Verifies that chunks with no parent identifier are treated as distinct documents.
    /// </summary>
    [Fact]
    public void CreateModelResult_TreatsChunksWithoutAParentAsSeparateDocuments()
    {
        var response = new SharePointSearchResponse(
            "leave",
            [
                CreateResult("c1", string.Empty, 3.0d),
                CreateResult("c2", string.Empty, 2.9d),
            ]);

        var result = SearchHrPolicyDocumentsTool.CreateModelResult(response, CreateSettings());

        Assert.Equal(2, result.ReturnedCount);
    }

    /// <summary>
    /// Verifies that the configured result ceiling is honoured.
    /// </summary>
    [Fact]
    public void CreateModelResult_LimitsResultsToTheConfiguredCeiling()
    {
        var response = new SharePointSearchResponse(
            "leave",
            [
                CreateResult("c1", "doc-1", 3.9d),
                CreateResult("c2", "doc-2", 3.8d),
                CreateResult("c3", "doc-3", 3.7d),
                CreateResult("c4", "doc-4", 3.6d),
                CreateResult("c5", "doc-5", 3.5d),
            ]);

        var result = SearchHrPolicyDocumentsTool.CreateModelResult(response, CreateSettings());

        Assert.Equal(5, result.RetrievedCount);
        Assert.Equal(3, result.ReturnedCount);
    }

    /// <summary>
    /// Verifies that the serialized payload is reduced when it exceeds the configured budget.
    /// </summary>
    [Fact]
    public void CreateModelResult_ReducesThePayloadToFitTheBudget()
    {
        var content = new string('x', 3_000);
        var response = new SharePointSearchResponse(
            "leave",
            [
                CreateResult("c1", "doc-1", 3.9d, content: content),
                CreateResult("c2", "doc-2", 3.8d, content: content),
                CreateResult("c3", "doc-3", 3.7d, content: content),
            ]);

        var result = SearchHrPolicyDocumentsTool.CreateModelResult(response, CreateSettings(2_000));

        Assert.Equal("success", result.Status);
        Assert.All(result.Results, item => Assert.True(item.Content.Length < content.Length));
    }

    /// <summary>
    /// Verifies that indexed file names become usable link labels.
    /// </summary>
    /// <param name="title">The indexed title.</param>
    /// <param name="expected">The expected label.</param>
    [Theory]
    [InlineData("Sickness_Absence_Policy_v3.docx", "Sickness Absence Policy v3")]
    [InlineData("Performance Review Guide.pdf", "Performance Review Guide")]
    [InlineData("Shared Parental Leave.ASPX", "Shared Parental Leave")]
    [InlineData("Policy 1.2", "Policy 1.2")]
    [InlineData("", "")]
    public void NormaliseTitle_ProducesActionableLabels(string title, string expected) =>
        Assert.Equal(expected, SearchHrPolicyDocumentsTool.NormaliseTitle(title, 500));

    /// <summary>
    /// Verifies that the current indexer's Graph drive path becomes a canonical SharePoint URL.
    /// </summary>
    [Fact]
    public void CreateCanonicalUrl_MapsGraphDrivePathToSharePointLibrary()
    {
        var result = SharePointSearchClient.CreateCanonicalUrl(
            "/drives/drive-id/root:/fr/performance review.pdf",
            "https://contoso.sharepoint.com/sites/hr/Shared Documents");

        Assert.Equal(
            "https://contoso.sharepoint.com/sites/hr/Shared%20Documents/fr/performance%20review.pdf",
            result);
    }

    /// <summary>
    /// Verifies that an indexed canonical HTTPS URL is not rewritten.
    /// </summary>
    [Fact]
    public void CreateCanonicalUrl_PreservesAbsoluteHttpsUrl()
    {
        const string url = "https://contoso.sharepoint.com/sites/hr/policy.pdf";

        Assert.Equal(url, SharePointSearchClient.CreateCanonicalUrl(url, string.Empty));
    }

    /// <summary>
    /// Verifies that a valid <c>weburi</c> HTTPS URL is used as the citation URL directly,
    /// without going through <c>metadata_spo_item_path</c> conversion.
    /// </summary>
    [Fact]
    public void ResolveUrl_PrefersWebUriWhenItIsAValidHttpsUrl()
    {
        const string webUri = "https://contoso.sharepoint.com/sites/hr/Policies/policy.pdf";
        const string itemPath = "/drives/drive-id/root:/Policies/policy.pdf";

        var result = SharePointSearchClient.ResolveUrl(webUri, itemPath, "https://contoso.sharepoint.com/sites/hr/Docs");

        Assert.Equal(webUri, result);
    }

    /// <summary>
    /// Verifies that an empty <c>weburi</c> causes the fallback path to use canonical URL conversion.
    /// </summary>
    [Fact]
    public void ResolveUrl_FallsBackToCanonicalUrlWhenWebUriIsEmpty()
    {
        var result = SharePointSearchClient.ResolveUrl(
            string.Empty,
            "/drives/drive-id/root:/policy.pdf",
            "https://contoso.sharepoint.com/sites/hr/Shared Documents");

        Assert.Equal(
            "https://contoso.sharepoint.com/sites/hr/Shared%20Documents/policy.pdf",
            result);
    }

    /// <summary>
    /// Verifies that a non-HTTPS or non-absolute <c>weburi</c> causes the fallback path to be used.
    /// </summary>
    /// <param name="webUri">The invalid or non-HTTPS <c>weburi</c> value.</param>
    [Theory]
    [InlineData("http://not-https.example.com/file.pdf")]
    [InlineData("ftp://file-transfer.example.com/file.pdf")]
    [InlineData("not-a-url-at-all")]
    [InlineData("")]
    public void ResolveUrl_FallsBackToCanonicalUrlWhenWebUriIsNotValidHttps(string webUri)
    {
        var result = SharePointSearchClient.ResolveUrl(
            webUri,
            "/drives/drive-id/root:/file.pdf",
            "https://contoso.sharepoint.com/sites/hr/Shared Documents");

        Assert.Equal(
            "https://contoso.sharepoint.com/sites/hr/Shared%20Documents/file.pdf",
            result);
    }

    /// <summary>
    /// Verifies that when both <c>weburi</c> and <c>CanonicalUrlBase</c> are absent the raw
    /// item path is returned unchanged (graceful degradation — citation URLs will be filtered
    /// out by <c>IsValidCanonicalUrl</c>, preserving safe behaviour).
    /// </summary>
    [Fact]
    public void ResolveUrl_ReturnsRawItemPathWhenNeitherSourceIsUsable()
    {
        const string itemPath = "/drives/drive-id/root:/file.pdf";

        var result = SharePointSearchClient.ResolveUrl(string.Empty, itemPath, string.Empty);

        Assert.Equal(itemPath, result);
    }

    private static SharePointSearchOptions CreateSettings(int maximumPayloadCharacters = 10_000) => new()
    {
        MinimumRerankerScore = 1.8d,
        MaxLlmResults = 3,
        MaxLlmResultContentCharacters = 2_500,
        MaxLlmPayloadCharacters = maximumPayloadCharacters,
        MaxTitleCharacters = 500,
    };

    private static SharePointSearchResult CreateResult(
        string chunkId,
        string parentId,
        double score,
        string url = "https://contoso.sharepoint.com/sites/intranet-dept-hr/Policies/policy.docx",
        string title = "Sickness Absence Policy.docx",
        string content = "Approved guidance content.") =>
        new(chunkId, parentId, title, content, url, score);
}
