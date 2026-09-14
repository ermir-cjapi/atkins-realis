namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Defines environment boundaries for sensitive or model-controlled agent capabilities.
/// </summary>
internal static class AgentRuntimePolicy
{
    /// <summary>
    /// Determines whether complete model payloads may be written to local diagnostics.
    /// </summary>
    internal static bool EnablePayloadLogging(IHostEnvironment environment) =>
        environment.IsDevelopment();

    /// <summary>
    /// Determines whether the model may invoke SharePoint retrieval directly.
    /// Production API retrieval is orchestrated by <see cref="AgentChatService"/> instead.
    /// </summary>
    internal static bool EnableModelControlledSharePointRetrieval(IHostEnvironment environment) =>
        environment.IsDevelopment();
}