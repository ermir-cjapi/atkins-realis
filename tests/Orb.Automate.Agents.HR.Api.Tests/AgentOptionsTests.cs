using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the default HR agent configuration.
/// </summary>
public sealed class AgentOptionsTests
{
    /// <summary>
    /// Verifies that the policy-grounded HR prompt is used by default.
    /// </summary>
    [Fact]
    public void Instructions_DefaultToHrPolicyPrompt()
    {
        var options = new AgentOptions();

        Assert.Same(HrAgentInstructions.Value, options.Instructions);
        Assert.Contains("[EMPLOYEE CONTEXT (verified by the application)]", options.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("get_employee_country", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Never answer from memory or general knowledge", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("I can answer questions about HR policies and processes, but I can't access personal records", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("That's outside what I can help with. I'm here for HR policy and process questions.", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("I couldn't find this in the approved HR guidance I have access to, so I don't want to guess.", options.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("point them to Workday", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Would you like me to...", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Need more help?", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that missing guidance does not override the supported drafting workflows.
    /// </summary>
    [Fact]
    public void Instructions_PreserveDraftingWhenGuidanceIsMissing()
    {
        var instructions = new AgentOptions().Instructions;

        Assert.Contains("Drafting exceptions take precedence over the fallback-only rule", instructions, StringComparison.Ordinal);
        Assert.Contains("still offer an email draft for an in-scope action request", instructions, StringComparison.Ordinal);
        Assert.Contains("produce an accepted email draft using process-neutral language", instructions, StringComparison.Ordinal);
        Assert.Contains("produce a SMART objective using the SMART rubric", instructions, StringComparison.Ordinal);
        Assert.Contains("apply the per-topic fallback rules to that question while preserving the draft", instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that a failed topic does not suppress grounded sections or their normal footer.
    /// </summary>
    [Fact]
    public void Instructions_ApplyFallbackPerTopic()
    {
        var instructions = new AgentOptions().Instructions;

        Assert.Contains("Apply the per-topic fallback rules and drafting exceptions", instructions, StringComparison.Ordinal);
        Assert.Contains("Do not discard grounded answers because another topic has no results", instructions, StringComparison.Ordinal);
        Assert.Contains("For each unanswered topic, include the fallback message below in that topic's section", instructions, StringComparison.Ordinal);
        Assert.Contains("Only when no requested topic can be answered from relevant approved guidance and no drafting exception applies", instructions, StringComparison.Ordinal);
        Assert.Contains("Keep the normal next-step suggestion and footer for a substantive, partially grounded response", instructions, StringComparison.Ordinal);
        Assert.Contains("A standalone fallback-only response for missing approved guidance (not a partially grounded response)", instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt names only the registered knowledge tool and the statuses it can
    /// actually return, so the agent cannot call non-existent tools or wait for impossible statuses.
    /// </summary>
    [Fact]
    public void Instructions_NameOnlyTheRegisteredKnowledgeTool()
    {
        var options = new AgentOptions();

        Assert.Contains("search_hr_knowledge", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("no_results", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("unavailable", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("invalid_query", options.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("search_hr_policy_documents_in_sharepoint", options.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("get_scoped_knowledge_articles_in_servicenow", options.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("identity_unavailable", options.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("ledger", options.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that the prompt contains the email-draft fence tag so the agent knows the
    /// required output format when drafting an email.
    /// </summary>
    [Fact]
    public void Instructions_ContainEmailDraftFenceTag()
    {
        var options = new AgentOptions();

        Assert.Contains("```email-draft", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt names all permitted tone values.
    /// </summary>
    [Fact]
    public void Instructions_ContainAllPermittedToneValues()
    {
        var options = new AgentOptions();

        Assert.Contains("professional", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("empathetic", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("factual", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt contains the regenerate marker strings so the model can
    /// identify a regenerate turn from the injected prompt context.
    /// </summary>
    [Fact]
    public void Instructions_ContainRegenerateMarkers()
    {
        var options = new AgentOptions();

        Assert.Contains("[PREVIOUS DRAFT]", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("[REGENERATE INSTRUCTION]", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt instructs the agent to retrieve policy grounding before drafting,
    /// enforcing the knowledge-first safety constraint for email content.
    /// </summary>
    [Fact]
    public void Instructions_RequirePolicyGroundingBeforeDrafting()
    {
        var options = new AgentOptions();

        Assert.Contains("attempt retrieval from SharePoint and ServiceNow", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt prohibits including the standard footer after an email draft,
    /// because the Adaptive Card provides copy and regenerate actions instead.
    /// </summary>
    [Fact]
    public void Instructions_SuppressFooterAfterEmailDraft()
    {
        var options = new AgentOptions();

        Assert.Contains("Adaptive Card", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt contains the smart-objective fence tag so the agent knows the
    /// required output format when writing a SMART objective.
    /// </summary>
    [Fact]
    public void Instructions_ContainSmartObjectiveFenceTag()
    {
        var options = new AgentOptions();

        Assert.Contains("```smart-objective", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt names all five SMART criteria so the model applies the full rubric.
    /// </summary>
    [Fact]
    public void Instructions_ContainAllSmartCriteria()
    {
        var options = new AgentOptions();

        Assert.Contains("Specific", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Measurable", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Achievable", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Relevant", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Time-bound", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt contains the SMART objective regenerate marker strings so the
    /// model can identify a regenerate turn from the injected prompt context.
    /// </summary>
    [Fact]
    public void Instructions_ContainSmartObjectiveRegenerateMarkers()
    {
        var options = new AgentOptions();

        Assert.Contains("[PREVIOUS OBJECTIVE]", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt explicitly supports the two refinement instructions called out in
    /// the feature requirements: making an objective more measurable and adding a timeframe.
    /// </summary>
    [Fact]
    public void Instructions_SupportCommonSmartObjectiveRefinements()
    {
        var options = new AgentOptions();

        Assert.Contains("make it more measurable", options.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add a timeframe", options.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that the prompt instructs the agent not to invent business priorities or
    /// guarantee performance ratings when drafting a SMART objective.
    /// </summary>
    [Fact]
    public void Instructions_ConstrainSmartObjectiveSafety()
    {
        var options = new AgentOptions();

        Assert.Contains("Do not invent", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("cannot approve, weight, or guarantee", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt discourages a repetitive "By [date], …" template for every objectiveText.
    /// </summary>
    [Fact]
    public void Instructions_DiscourageRepetitiveObjectiveTextTemplate()
    {
        var options = new AgentOptions();

        Assert.Contains("Do not", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("begin every objective with the same pattern", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that only one smart-objective fence may be emitted per model turn.
    /// </summary>
    [Fact]
    public void Instructions_LimitOneSmartObjectiveFencePerTurn()
    {
        var options = new AgentOptions();

        Assert.Contains("at most one", options.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smart-objective", options.Instructions, StringComparison.Ordinal);
    }
}