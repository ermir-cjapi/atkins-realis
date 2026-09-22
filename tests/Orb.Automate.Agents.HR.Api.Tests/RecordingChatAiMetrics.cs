using Orb.Automate.Agents.HR.Api.Diagnostics;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Captures metric calls so tests can assert emission without a live meter.</summary>
internal sealed class RecordingChatAiMetrics : IChatAiMetrics
{
    public List<ProviderCallObservation> ProviderCalls { get; } = [];

    public List<(TimeSpan Duration, string Mode, string Outcome, string Deployment, AiTurnSnapshot Snapshot)> Turns { get; } = [];

    public List<(TimeSpan Duration, string Source, int Retrieved, int Returned)> Grounding { get; } = [];

    public List<(string Tool, string Mode, string Outcome, string FailureKind, TimeSpan Duration)> Tools { get; } = [];

    public List<(string Outcome, double? Coverage)> Citations { get; } = [];

    public List<string> Classifications { get; } = [];

    public List<TimeSpan> SseFirstFlushes { get; } = [];

    public List<string> SseOutcomes { get; } = [];

    public void RecordProviderCall(ProviderCallObservation call) => ProviderCalls.Add(call);

    public void RecordTurn(TimeSpan duration, string mode, string outcome, string configuredDeployment, AiTurnSnapshot snapshot) =>
        Turns.Add((duration, mode, outcome, configuredDeployment, snapshot));

    public void RecordGrounding(TimeSpan duration, string source, int retrieved, int returned) =>
        Grounding.Add((duration, source, retrieved, returned));

    public void RecordTool(string tool, string invocationMode, string outcome, string failureKind, TimeSpan duration) =>
        Tools.Add((tool, invocationMode, outcome, failureKind, duration));

    public void RecordCitation(string outcome, double? coverage) => Citations.Add((outcome, coverage));

    public void RecordClassification(string outcome) => Classifications.Add(outcome);

    public void RecordSseFirstFlush(TimeSpan elapsed) => SseFirstFlushes.Add(elapsed);

    public void RecordSseOutcome(string outcome) => SseOutcomes.Add(outcome);
}
