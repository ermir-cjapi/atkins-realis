namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// Collects every physical provider-call observation belonging to one service turn.
/// </summary>
public interface IAiTurnTelemetryContext
{
    /// <summary>Starts an isolated per-turn accounting scope.</summary>
    /// <returns>A scope that restores the previously active turn when disposed.</returns>
    IDisposable BeginTurn();

    /// <summary>Assigns the next provider-call sequence number for the active turn.</summary>
    /// <returns>The increasing sequence number, or zero when no turn is active.</returns>
    long NextSequence();

    /// <summary>Records one completed provider-call observation into the active turn.</summary>
    /// <param name="observation">The observation to record.</param>
    void Record(ProviderCallObservation observation);

    /// <summary>Gets an immutable snapshot of the active turn.</summary>
    AiTurnSnapshot Snapshot { get; }
}

/// <summary>
/// Async-flow-local implementation that is safe for singleton registration and for concurrent or
/// nested turns.
/// </summary>
public sealed class AiTurnTelemetryContext : IAiTurnTelemetryContext
{
    private readonly AsyncLocal<State?> current = new();

    /// <inheritdoc />
    public IDisposable BeginTurn()
    {
        var previous = current.Value;
        current.Value = new State();
        return new TelemetryScope(() => current.Value = previous);
    }

    /// <inheritdoc />
    public long NextSequence() => current.Value is { } state ? Interlocked.Increment(ref state.Sequence) : 0;

    /// <inheritdoc />
    public void Record(ProviderCallObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (current.Value is not { } state)
        {
            return;
        }

        lock (state.Gate)
        {
            state.Calls.Add(observation);
        }
    }

    /// <inheritdoc />
    public AiTurnSnapshot Snapshot
    {
        get
        {
            if (current.Value is not { } state)
            {
                return AiTurnSnapshot.Empty;
            }

            ProviderCallObservation[] calls;
            lock (state.Gate)
            {
                calls = [.. state.Calls.OrderBy(call => call.Sequence)];
            }

            if (calls.Length == 0)
            {
                return AiTurnSnapshot.Empty;
            }

            var models = DistinctModels(calls);
            return new(
                CreateComponent(AiComponents.MainAgent, calls),
                CreateComponent(AiComponents.IntentClassifier, calls),
                TokenUsageSnapshot.Sum(calls.Select(call => call.Usage)),
                models,
                models.Count > 1,
                calls);
        }
    }

    private static AiComponentSnapshot CreateComponent(string component, IReadOnlyList<ProviderCallObservation> calls)
    {
        var selected = calls
            .Where(call => string.Equals(call.Component, component, StringComparison.Ordinal))
            .ToArray();
        if (selected.Length == 0)
        {
            return AiComponentSnapshot.Empty(component);
        }

        var final = selected.LastOrDefault(call => call.Outcome == ProviderCallOutcome.Succeeded) ?? selected[^1];
        return new(
            component,
            selected.Length,
            selected.Count(call => call.Usage.Available),
            TokenUsageSnapshot.Sum(selected.Select(call => call.Usage)),
            final.ModelId,
            final.ResponseId,
            selected.FirstOrDefault(call => call.TimeToFirstText.HasValue)?.TimeToFirstText,
            DistinctModels(selected));
    }

    private static IReadOnlyList<string> DistinctModels(IReadOnlyList<ProviderCallObservation> calls) =>
    [
        .. calls
            .Select(call => call.ModelId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
    ];

    private sealed class State
    {
        public object Gate { get; } = new();

        public List<ProviderCallObservation> Calls { get; } = [];

        public long Sequence;
    }
}
