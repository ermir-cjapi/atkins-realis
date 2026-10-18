using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.Search;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests merging, de-duplication, and conflict detection in <see cref="KnowledgeMerger"/>.
/// </summary>
public sealed class KnowledgeMergerTests
{
    private const string SpUrl = "https://contoso.sharepoint.com/sites/hr/policy.aspx";

    /// <summary>
    /// Verifies that policies whose titles differ by a country are not merged, even though their
    /// title similarity exceeds the de-duplication threshold.
    /// </summary>
    [Fact]
    public void MergeAndRank_DoesNotMergeTitlesThatDifferByCountry()
    {
        var result = KnowledgeMerger.MergeAndRank(
            SharePoint(Sp("UK Leave of Absence Policy", "UK staff receive 10 days.")),
            ServiceNow(Sn("US Leave of Absence Policy", "US staff receive 5 days.")),
            new SharePointSearchOptions());

        Assert.Equal(2, result.Passages.Count);
        Assert.DoesNotContain(result.Passages, p => p.Source == "both");
    }

    /// <summary>
    /// Verifies that titles differing only by generic words are merged into a single passage.
    /// </summary>
    [Fact]
    public void MergeAndRank_MergesTitlesThatDifferOnlyByGenericWords()
    {
        var result = KnowledgeMerger.MergeAndRank(
            SharePoint(Sp("UK Annual Leave Policy", "UK staff receive 25 days.")),
            ServiceNow(Sn("UK Annual Leave", "UK staff receive 25 days.")),
            new SharePointSearchOptions());

        var passage = Assert.Single(result.Passages);
        Assert.Equal("both", passage.Source);
        Assert.Equal("KB0010001", passage.KbNumber);
        Assert.False(passage.Conflict);
    }

    /// <summary>
    /// Verifies that a conflicting merged passage keeps the ServiceNow text so both values can be disclosed.
    /// </summary>
    [Fact]
    public void MergeAndRank_ConflictKeepsServiceNowContent()
    {
        var result = KnowledgeMerger.MergeAndRank(
            SharePoint(Sp("UK Annual Leave Policy", "UK staff receive 25 days.")),
            ServiceNow(Sn("UK Annual Leave", "UK staff receive 22 days.")),
            new SharePointSearchOptions());

        var passage = Assert.Single(result.Passages);
        Assert.True(passage.Conflict);
        Assert.Equal("UK staff receive 25 days.", passage.Content);
        Assert.Equal("UK staff receive 22 days.", passage.ConflictingContent);
        Assert.Contains("KB0010001", passage.ConflictNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that an incidental shared number (a year) does not mask differing quantities.
    /// </summary>
    [Fact]
    public void HasNumericConflict_DetectsDifferentQuantitiesDespiteSharedYear()
    {
        Assert.True(KnowledgeMerger.HasNumericConflict(
            "From 2026, UK staff receive 25 days of annual leave.",
            "From 2026, UK staff receive 22 days of annual leave."));
    }

    /// <summary>
    /// Verifies that matching quantities, including singular and plural units, are not a conflict.
    /// </summary>
    [Fact]
    public void HasNumericConflict_IgnoresMatchingQuantities()
    {
        Assert.False(KnowledgeMerger.HasNumericConflict(
            "From 2026, staff receive 25 days of leave.",
            "Staff receive 25 days of leave plus 1 day for moving house."));
    }

    /// <summary>
    /// Verifies that a gate at or above the normalisation ceiling yields finite, ordered scores.
    /// </summary>
    [Theory]
    [InlineData(5.0, 5.0, 1.0)]
    [InlineData(6.0, 7.0, 1.0)]
    [InlineData(6.0, 5.5, 0.0)]
    public void NormaliseSpScore_HandlesGateAtOrAboveCeiling(double minGate, double score, double expected)
    {
        Assert.Equal(expected, KnowledgeMerger.NormaliseSpScore(score, minGate));
    }

    private static SearchHrPolicyDocumentsResult SharePoint(params HrPolicyDocumentResult[] results) =>
        new("success", string.Empty, "leave", results.Length, results.Length, results);

    private static GetScopedKnowledgeArticlesResult ServiceNow(params CompactServiceNowSearchResult[] results) =>
        new("success", string.Empty, "leave", results.Length, results.Length, results);

    private static HrPolicyDocumentResult Sp(string title, string content, double score = 3.0) =>
        new("chunk-1", "parent-1", title, content, SpUrl, score, "1", DateTimeOffset.UtcNow, "UK", "UK", "All");

    private static CompactServiceNowSearchResult Sn(string title, string content) =>
        new("sys-1", "kb_knowledge", title, content, "KB0010001", "Leave");
}
