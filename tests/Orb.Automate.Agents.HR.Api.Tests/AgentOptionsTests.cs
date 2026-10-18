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
        Assert.Contains("get_employee_country", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("get_scoped_knowledge_articles_in_servicenow", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Never answer from memory or general knowledge", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("I can only help with Performance Reviews", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Would you like me to...", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Need more help?", options.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt names the SharePoint retrieval tool and its refusal statuses, so the
    /// agent cannot treat SharePoint content as ambient knowledge.
    /// </summary>
    [Fact]
    public void Instructions_NameTheSharePointRetrievalTool()
    {
        var options = new AgentOptions();

        Assert.Contains("search_hr_policy_documents_in_sharepoint", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("identity_unavailable", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("no_results", options.Instructions, StringComparison.Ordinal);
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