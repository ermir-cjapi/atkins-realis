using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Agent;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the smart-objective fence extraction logic in <see cref="AgentChatService"/>.
/// </summary>
public sealed class SmartObjectiveParserTests
{
    // Minimal valid fence embedded in a realistic agent response.
    private const string ValidFenceResponse = """
        I've turned that into a SMART objective you can use for your Performance Review.

        ```smart-objective
        {"objectiveText":"Reduce average time-to-hire for engineering roles from 45 to 30 days by 31 December 2026.","specific":"Reduce time-to-hire for engineering roles","measurable":"From 45 to 30 days","achievable":"Builds on the recruiting process improvements already underway","relevant":"Supports the team's hiring priority for this year","timeBound":"By 31 December 2026","policySourceUrls":["https://contoso.sharepoint.com/sites/hr/objectives.aspx"]}
        ```
        """;

    /// <summary>
    /// Verifies that a well-formed fence block is extracted and deserialised.
    /// </summary>
    [Fact]
    public void ExtractSmartObjective_ReturnsObjective_WhenFenceIsValid()
    {
        var (_, objective) = AgentChatService.ExtractSmartObjective(ValidFenceResponse, NullLogger.Instance);

        Assert.NotNull(objective);
        Assert.Contains("time-to-hire", objective.ObjectiveText, StringComparison.Ordinal);
        Assert.Equal("Reduce time-to-hire for engineering roles", objective.Specific);
        Assert.Equal("From 45 to 30 days", objective.Measurable);
        Assert.Equal("Builds on the recruiting process improvements already underway", objective.Achievable);
        Assert.Equal("Supports the team's hiring priority for this year", objective.Relevant);
        Assert.Equal("By 31 December 2026", objective.TimeBound);
        // No grounding was supplied, so there is nothing to verify the URL against —
        // FilterPolicySourceUrls drops all URLs rather than passing them through unchecked.
        Assert.Null(objective.PolicySourceUrls);
    }

    /// <summary>
    /// Verifies that the fence block is removed from the returned message text.
    /// </summary>
    [Fact]
    public void ExtractSmartObjective_RemovesFenceFromCleanedText()
    {
        var (cleanedText, _) = AgentChatService.ExtractSmartObjective(ValidFenceResponse, NullLogger.Instance);

        Assert.DoesNotContain("```smart-objective", cleanedText, StringComparison.Ordinal);
        Assert.DoesNotContain("```", cleanedText, StringComparison.Ordinal);
        Assert.Contains("I've turned that into a SMART objective", cleanedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that plain text responses (no fence) are returned unchanged with a null objective.
    /// </summary>
    [Fact]
    public void ExtractSmartObjective_ReturnsOriginalText_WhenNoFencePresent()
    {
        const string plainResponse = "You can add objectives in Workday under the Performance Review tile.";

        var (cleanedText, objective) = AgentChatService.ExtractSmartObjective(plainResponse, NullLogger.Instance);

        Assert.Equal(plainResponse, cleanedText);
        Assert.Null(objective);
    }

    /// <summary>
    /// Verifies that a fence containing malformed JSON is treated as a parse failure:
    /// the original text is returned unchanged and no objective is produced.
    /// </summary>
    [Fact]
    public void ExtractSmartObjective_ReturnsOriginalText_WhenFenceJsonIsMalformed()
    {
        const string malformedFence = """
            Here is your objective.

            ```smart-objective
            {this is not valid json}
            ```
            """;

        var (cleanedText, objective) = AgentChatService.ExtractSmartObjective(malformedFence, NullLogger.Instance);

        Assert.Null(objective);
        Assert.Equal(malformedFence, cleanedText);
    }

    /// <summary>
    /// Verifies that a fence whose JSON is valid but missing the required objectiveText field
    /// is treated as a parse failure.
    /// </summary>
    [Fact]
    public void ExtractSmartObjective_ReturnsOriginalText_WhenObjectiveTextMissing()
    {
        const string incompleteJson = """
            Here is your objective.

            ```smart-objective
            {"specific":"Something specific"}
            ```
            """;

        var (cleanedText, objective) = AgentChatService.ExtractSmartObjective(incompleteJson, NullLogger.Instance);

        Assert.Null(objective);
        Assert.Equal(incompleteJson, cleanedText);
    }

    /// <summary>
    /// Verifies that a fence with no policy source URLs produces an objective with a null
    /// PolicySourceUrls property rather than an empty list.
    /// </summary>
    [Fact]
    public void ExtractSmartObjective_SetsPolicySourceUrlsToNull_WhenArrayIsEmpty()
    {
        const string fenceWithEmptyUrls = """
            Here is your objective.

            ```smart-objective
            {"objectiveText":"Deliver two internal training sessions on data literacy by 30 June 2026.","policySourceUrls":[]}
            ```
            """;

        var (_, objective) = AgentChatService.ExtractSmartObjective(fenceWithEmptyUrls, NullLogger.Instance);

        Assert.NotNull(objective);
        Assert.Null(objective.PolicySourceUrls);
    }

    /// <summary>
    /// Verifies that the optional SMART breakdown fields are null, not empty strings, when the
    /// model omits them or emits blank values.
    /// </summary>
    [Fact]
    public void ExtractSmartObjective_LeavesOptionalFieldsNull_WhenAbsentOrBlank()
    {
        const string minimalFence = """
            Here is your objective.

            ```smart-objective
            {"objectiveText":"Deliver two internal training sessions on data literacy by 30 June 2026.","specific":"   ","timeBound":""}
            ```
            """;

        var (_, objective) = AgentChatService.ExtractSmartObjective(minimalFence, NullLogger.Instance);

        Assert.NotNull(objective);
        Assert.Null(objective.Specific);
        Assert.Null(objective.Measurable);
        Assert.Null(objective.Achievable);
        Assert.Null(objective.Relevant);
        Assert.Null(objective.TimeBound);
    }

    /// <summary>
    /// Verifies that the fence is matched regardless of CRLF or LF line endings.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ExtractSmartObjective_HandlesLineEndingVariants(string lineEnding)
    {
        var response =
            $"Here is your objective.{lineEnding}{lineEnding}```smart-objective{lineEnding}" +
            $"{{\"objectiveText\":\"Deliver two internal training sessions on data literacy by 30 June 2026.\",\"timeBound\":\"By 30 June 2026\"}}{lineEnding}```{lineEnding}";

        var (cleanedText, objective) = AgentChatService.ExtractSmartObjective(response, NullLogger.Instance);

        Assert.NotNull(objective);
        Assert.DoesNotContain("```smart-objective", cleanedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that policy source URLs are filtered to approved ledger URLs when grounding is present.
    /// </summary>
    [Fact]
    public void ExtractSmartObjective_FiltersPolicySourceUrls_RemovingHallucinatedUrls()
    {
        const string approvedUrl = "https://contoso.sharepoint.com/sites/hr/objectives.aspx";
        const string hallucinatedUrl = "https://hallucinated.example.com/fake-policy";
        var response =
            "Here is your objective.\n\n```smart-objective\n" +
            $"{{\"objectiveText\":\"Deliver two internal training sessions on data literacy by 30 June 2026.\"," +
            $"\"policySourceUrls\":[\"{approvedUrl}\",\"{hallucinatedUrl}\"]}}\n```";

        var grounding = new SearchHrPolicyDocumentsResult(
            "success",
            "Search succeeded.",
            "objectives",
            1,
            1,
            [
                new HrPolicyDocumentResult(
                    "chunk-1",
                    "document-1",
                    "Setting SMART Objectives",
                    "Approved objective-setting guidance.",
                    approvedUrl,
                    3.5,
                    "1",
                    null,
                    "UK",
                    "UK",
                    "Employees")
            ]);

        var (_, objective) = AgentChatService.ExtractSmartObjective(
            response, NullLogger.Instance, grounding);

        Assert.NotNull(objective);
        Assert.NotNull(objective.PolicySourceUrls);
        Assert.Single(objective.PolicySourceUrls);
        Assert.Equal(approvedUrl, objective.PolicySourceUrls[0]);
    }

    /// <summary>
    /// Verifies that policy source URLs are dropped entirely when no grounding context is
    /// available. Without an approved list to filter against, all URLs from the model (including
    /// client-echoed ones from previousObjective.policySourceUrls) would pass through unchecked
    /// and be rendered as clickable Action.OpenUrl links in the Adaptive Card — a phishing risk.
    /// </summary>
    [Fact]
    public void ExtractSmartObjective_DropsPolicySourceUrls_WhenNoGroundingAvailable()
    {
        const string anyUrl = "https://could-be-anything.example.com/link";
        var response =
            "Here is your objective.\n\n```smart-objective\n" +
            $"{{\"objectiveText\":\"Deliver two internal training sessions on data literacy by 30 June 2026.\"," +
            $"\"policySourceUrls\":[\"{anyUrl}\"]}}\n```";

        // No grounding and no ServiceNow sources supplied.
        var (_, objective) = AgentChatService.ExtractSmartObjective(
            response, NullLogger.Instance, grounding: null, serviceNowSources: null);

        Assert.NotNull(objective);
        // With nothing to verify against, all URLs must be dropped rather than passed through.
        Assert.Null(objective.PolicySourceUrls);
    }
}
