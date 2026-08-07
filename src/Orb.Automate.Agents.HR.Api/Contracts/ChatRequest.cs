namespace Orb.Automate.Agents.HR.Api.Contracts;

/// <summary>
/// Represents a chat request submitted by a client.
/// </summary>
/// <param name="Message">The user message to send to the agent.</param>
public sealed record ChatRequest(string? Message);
