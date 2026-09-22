namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>Bounded component names used at the physical AI provider boundary.</summary>
public static class AiComponents
{
    /// <summary>The main answering agent, including every Agent Framework tool-loop round.</summary>
    public const string MainAgent = "main_agent";

    /// <summary>The separate telemetry intent-classification call.</summary>
    public const string IntentClassifier = "intent_classifier";
}
