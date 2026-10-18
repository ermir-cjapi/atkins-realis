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

    }