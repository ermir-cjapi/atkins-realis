using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Agent;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the email-draft fence extraction logic in <see cref="AgentChatService"/>.
/// </summary>
public sealed class EmailDraftParserTests
{
    // Minimal valid fence embedded in a realistic agent response.
    private const string ValidFenceResponse = """
        I can't approve that, but here is a draft email you can send to your HR Business Partner.

        ```email-draft
        {"subject":"Request for Additional Annual Leave","body":"Dear [Name],\n\nI am writing to request additional annual leave.\n\nKind regards,\n[Your name]","suggestedRecipient":"your HR Business Partner","tone":"professional","policySourceUrls":["https://contoso.sharepoint.com/sites/hr/leave.aspx"]}
        ```
        """;

    /// <summary>
    /// Verifies that a well-formed fence block is extracted and deserialised.
    /// </summary>
    [Fact]
    public void ExtractEmailDraft_ReturnsDraft_WhenFenceIsValid()
    {
        var (_, draft) = AgentChatService.ExtractEmailDraft(ValidFenceResponse, NullLogger.Instance);

        Assert.NotNull(draft);
        Assert.Equal("Request for Additional Annual Leave", draft.Subject);
        Assert.Contains("Dear [Name]", draft.Body, StringComparison.Ordinal);
        Assert.Equal("your HR Business Partner", draft.SuggestedRecipient);
        Assert.Equal("professional", draft.Tone);
        // No grounding was supplied, so there is nothing to verify the URL against —
        // FilterPolicySourceUrls drops all URLs rather than passing them through unchecked.
        Assert.Null(draft.PolicySourceUrls);
    }

    /// <summary>
    /// Verifies that the fence block is removed from the returned message text.
    /// </summary>
    [Fact]
    public void ExtractEmailDraft_RemovesFenceFromCleanedText()
    {
        var (cleanedText, _) = AgentChatService.ExtractEmailDraft(ValidFenceResponse, NullLogger.Instance);

        Assert.DoesNotContain("```email-draft", cleanedText, StringComparison.Ordinal);
        Assert.DoesNotContain("```", cleanedText, StringComparison.Ordinal);
        Assert.Contains("I can't approve that", cleanedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that plain text responses (no fence) are returned unchanged with a null draft.
    /// </summary>
    [Fact]
    public void ExtractEmailDraft_ReturnsOriginalText_WhenNoFencePresent()
    {
        const string plainResponse = "Your annual leave entitlement is 25 days per year.";

        var (cleanedText, draft) = AgentChatService.ExtractEmailDraft(plainResponse, NullLogger.Instance);

        Assert.Equal(plainResponse, cleanedText);
        Assert.Null(draft);
    }

    /// <summary>
    /// Verifies that a fence containing malformed JSON is treated as a parse failure:
    /// the original text is returned unchanged and no draft is produced.
    /// </summary>
    [Fact]
    public void ExtractEmailDraft_ReturnsOriginalText_WhenFenceJsonIsMalformed()
    {
        const string malformedFence = """
            Here is your draft.

            ```email-draft
            {this is not valid json}
            ```
            """;

        var (cleanedText, draft) = AgentChatService.ExtractEmailDraft(malformedFence, NullLogger.Instance);

        Assert.Null(draft);
        Assert.Equal(malformedFence, cleanedText);
    }

    /// <summary>
    /// Verifies that a fence whose JSON is valid but missing required fields (subject, body)
    /// is treated as a parse failure.
    /// </summary>
    [Fact]
    public void ExtractEmailDraft_ReturnsOriginalText_WhenRequiredFieldsMissing()
    {
        const string incompleteJson = """
            Here is your draft.

            ```email-draft
            {"tone":"professional"}
            ```
            """;

        var (cleanedText, draft) = AgentChatService.ExtractEmailDraft(incompleteJson, NullLogger.Instance);

        Assert.Null(draft);
        Assert.Equal(incompleteJson, cleanedText);
    }

    /// <summary>
    /// Verifies that a fence with no policy source URLs produces a draft with a null
    /// PolicySourceUrls property rather than an empty list.
    /// </summary>
    [Fact]
    public void ExtractEmailDraft_SetsPolicySourceUrlsToNull_WhenArrayIsEmpty()
    {
        const string fenceWithEmptyUrls = """
            Here is your draft.

            ```email-draft
            {"subject":"Leave request","body":"Dear [Name],\n\nKind regards,\n[Your name]","suggestedRecipient":"your manager","tone":"empathetic","policySourceUrls":[]}
            ```
            """;

        var (_, draft) = AgentChatService.ExtractEmailDraft(fenceWithEmptyUrls, NullLogger.Instance);

        Assert.NotNull(draft);
        Assert.Null(draft.PolicySourceUrls);
    }

    /// <summary>
    /// Verifies that a missing tone field defaults to "professional".
    /// </summary>
    [Fact]
    public void ExtractEmailDraft_DefaultsToneToProfessional_WhenToneFieldIsAbsent()
    {
        const string fenceWithoutTone = """
            Here is your draft.

            ```email-draft
            {"subject":"Leave request","body":"Dear [Name],\n\nKind regards,\n[Your name]","suggestedRecipient":"your manager"}
            ```
            """;

        var (_, draft) = AgentChatService.ExtractEmailDraft(fenceWithoutTone, NullLogger.Instance);

        Assert.NotNull(draft);
        Assert.Equal("professional", draft.Tone);
    }

    /// <summary>
    /// Verifies that the fence is matched regardless of CRLF or LF line endings.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ExtractEmailDraft_HandlesLineEndingVariants(string lineEnding)
    {
        var response =
            $"Here is your draft.{lineEnding}{lineEnding}```email-draft{lineEnding}" +
            $"{{\"subject\":\"Leave request\",\"body\":\"Dear [Name],\\n\\nKind regards,\\n[Your name]\",\"suggestedRecipient\":\"your manager\",\"tone\":\"factual\"}}{lineEnding}```{lineEnding}";

        var (cleanedText, draft) = AgentChatService.ExtractEmailDraft(response, NullLogger.Instance);

        Assert.NotNull(draft);
        Assert.DoesNotContain("```email-draft", cleanedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that incremental streaming holds back fence openers and trailing incomplete backticks.
    /// </summary>
    [Theory]
    [InlineData("Here is a draft.", 16)]
    [InlineData("Hello `", 6)]
    [InlineData("Hello ``", 6)]
    [InlineData("Hello ```email-draft", 6)]
    [InlineData("```email-draft", 0)]
    public void GetSafeStreamingLength_HoldsBackFencePrefix(string text, int expected)
    {
        Assert.Equal(expected, AgentChatService.GetSafeStreamingLength(text));
    }

    /// <summary>
    /// Verifies that policy source URLs are dropped entirely when no grounding context is
    /// available. Without an approved list to filter against, all URLs from the model (including
    /// client-echoed ones from previousDraft.policySourceUrls) would pass through unchecked
    /// and be rendered as clickable Action.OpenUrl links in the Adaptive Card — a phishing risk.
    /// </summary>
    [Fact]
    public void ExtractEmailDraft_DropsPolicySourceUrls_WhenNoGroundingAvailable()
    {
        const string anyUrl = "https://could-be-anything.example.com/link";
        var response =
            "Here is your draft.\n\n```email-draft\n" +
            $"{{\"subject\":\"Leave Request\",\"body\":\"Dear [Name],\\n\\nKind regards,\\n[Your name]\"," +
            $"\"suggestedRecipient\":\"your manager\",\"tone\":\"professional\"," +
            $"\"policySourceUrls\":[\"{anyUrl}\"]}}\n```";

        // No grounding and no ServiceNow sources supplied.
        var (_, draft) = AgentChatService.ExtractEmailDraft(
            response, NullLogger.Instance, grounding: null, serviceNowSources: null);

        Assert.NotNull(draft);
        // With nothing to verify against, all URLs must be dropped rather than passed through.
        Assert.Null(draft.PolicySourceUrls);
    }
}
