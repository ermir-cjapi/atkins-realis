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
        Assert.Contains("Get Employee Country", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("get_scoped_knowledge_articles", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Never answer from memory or general knowledge", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("I can only help with Performance Reviews", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Would you like me to...", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Need more help?", options.Instructions, StringComparison.Ordinal);
    }
}