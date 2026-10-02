using System.ComponentModel.DataAnnotations;

namespace Orb.Automate.Agents.HR.Api.Options;

/// <summary>
/// Configuration for the HR chat agent behavior.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>
    /// The configuration section name for agent settings.
    /// </summary>
    public const string SectionName = "Agent";

    /// <summary>
    /// The agent name used when no name is supplied by configuration.
    /// </summary>
    public const string DefaultName = "Assist | HR";

    /// <summary>
    /// Gets the display name assigned to the agent.
    /// </summary>
    [Required]
    public string Name { get; init; } = DefaultName;

    /// <summary>
    /// Gets the system instructions supplied to the agent.
    /// </summary>
    [Required]
    public string Instructions { get; init; } = HrAgentInstructions.Value;

    /// <summary>
    /// Gets the maximum allowed number of characters in an input message.
    /// </summary>
    [Range(1, 100_000)]
    public int MaxInputCharacters { get; init; } = 12_000;
}
