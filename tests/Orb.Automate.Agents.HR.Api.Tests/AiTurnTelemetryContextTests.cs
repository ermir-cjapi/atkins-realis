using Microsoft.Extensions.AI;
using Orb.Automate.Agents.HR.Api.Diagnostics;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests per-turn provider accounting isolation and aggregation.</summary>
public sealed class AiTurnTelemetryContextTests
{
    /// <summary>Verifies a nested turn does not leak into or overwrite the enclosing turn.</summary>
    [Fact]
    public void NestedScopesRestoreOuterTurn()
    {
        var context = new AiTurnTelemetryContext();
        using var outer = context.BeginTurn();
        Record(context, AiComponents.MainAgent, 2, "outer");
        using (context.BeginTurn())
        {
            Record(context, AiComponents.IntentClassifier, 3, "inner");
            Assert.Equal(3, context.Snapshot.CombinedUsage.InputTokens);
            Assert.Equal("inner", context.Snapshot.Classifier.FinalModelId);
        }

        Assert.Equal(2, context.Snapshot.CombinedUsage.InputTokens);
        Assert.Equal("outer", context.Snapshot.MainAgent.FinalModelId);
        Assert.Equal(0, context.Snapshot.Classifier.CallCount);
    }

    /// <summary>Verifies concurrent turns accumulate independently.</summary>
    [Fact]
    public async Task ConcurrentScopesRemainIsolated()
    {
        var context = new AiTurnTelemetryContext();
        var first = CaptureAsync(context, 11, "first");
        var second = CaptureAsync(context, 17, "second");

        var snapshots = await Task.WhenAll(first, second);

        Assert.Equal(11, snapshots[0].CombinedUsage.InputTokens);
        Assert.Equal("first", snapshots[0].MainAgent.FinalModelId);
        Assert.Equal(17, snapshots[1].CombinedUsage.InputTokens);
        Assert.Equal("second", snapshots[1].MainAgent.FinalModelId);
    }

    /// <summary>Verifies observations made without an active turn are ignored.</summary>
    [Fact]
    public void RecordingOutsideATurnIsIgnored()
    {
        var context = new AiTurnTelemetryContext();

        Record(context, AiComponents.MainAgent, 5, "model");

        Assert.Same(AiTurnSnapshot.Empty, context.Snapshot);
        Assert.Equal(0, context.NextSequence());
    }

    /// <summary>Verifies the sequence orders observations and never repeats within a turn.</summary>
    [Fact]
    public void SequenceIsMonotonicWithinATurn()
    {
        var context = new AiTurnTelemetryContext();
        using var scope = context.BeginTurn();

        Record(context, AiComponents.MainAgent, 1, "model-a");
        Record(context, AiComponents.MainAgent, 1, "model-b");

        Assert.Equal([1, 2], context.Snapshot.Calls.Select(call => call.Sequence));
    }

    private static async Task<AiTurnSnapshot> CaptureAsync(AiTurnTelemetryContext context, long tokens, string model)
    {
        using var scope = context.BeginTurn();
        await Task.Yield();
        Record(context, AiComponents.MainAgent, tokens, model);
        return context.Snapshot;
    }

    private static void Record(AiTurnTelemetryContext context, string component, long tokens, string model) =>
        context.Record(new ProviderCallObservation(
            context.NextSequence(),
            component,
            false,
            TokenUsageSnapshot.From(new UsageDetails { InputTokenCount = tokens }),
            model,
            null,
            null,
            TimeSpan.Zero,
            null,
            ProviderCallOutcome.Succeeded));
}
