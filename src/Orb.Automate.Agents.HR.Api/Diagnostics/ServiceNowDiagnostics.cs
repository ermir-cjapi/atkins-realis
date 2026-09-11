using System.Diagnostics;
using System.Diagnostics.Metrics;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// Low-cardinality diagnostics for the ServiceNow grounding boundary.
/// </summary>
public static class ServiceNowDiagnostics
{
    /// <summary>The OpenTelemetry activity-source and meter name.</summary>
    public const string Name = "Orb.Automate.Agents.HR.ServiceNow";

    /// <summary>Activity source for logical ServiceNow searches.</summary>
    public static readonly ActivitySource ActivitySource = new(Name);

    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> LogicalDuration = Meter.CreateHistogram<double>("servicenow.search.duration", "ms");
    private static readonly Histogram<double> AttemptDuration = Meter.CreateHistogram<double>("servicenow.attempt.duration", "ms");
    private static readonly Histogram<double> RemainingBudget = Meter.CreateHistogram<double>("servicenow.budget.remaining", "ms");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("servicenow.attempt.count");
    private static readonly Counter<long> CacheOutcomes = Meter.CreateCounter<long>("servicenow.cache.count");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("servicenow.retry.count");
    private static readonly Counter<long> RateRejections = Meter.CreateCounter<long>("servicenow.rate_rejection.count");
    private static readonly Counter<long> CircuitTransitions = Meter.CreateCounter<long>("servicenow.circuit.transition.count");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("servicenow.failure.count");

    /// <summary>Records one logical search duration.</summary>
    public static void RecordLogicalDuration(TimeSpan elapsed, ServiceNowSearchSource source) =>
        LogicalDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("source", source.ToString().ToLowerInvariant()));

    /// <summary>Records one outbound attempt duration.</summary>
    public static void RecordAttempt(TimeSpan elapsed, string client) =>
        AttemptDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("client", client));

    /// <summary>Records one outbound attempt.</summary>
    public static void RecordAttempt(string client) => Attempts.Add(1, new KeyValuePair<string, object?>("client", client));

    /// <summary>Records the remaining aggregate budget.</summary>
    public static void RecordRemainingBudget(TimeSpan remaining) => RemainingBudget.Record(remaining.TotalMilliseconds);

    /// <summary>Records a bounded cache outcome.</summary>
    public static void RecordCache(string outcome) => CacheOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Records one retry.</summary>
    public static void RecordRetry(string client) => Retries.Add(1, new KeyValuePair<string, object?>("client", client));

    /// <summary>Records a local rate rejection.</summary>
    public static void RecordRateRejection() => RateRejections.Add(1);

    /// <summary>Records a circuit state transition.</summary>
    public static void RecordCircuitTransition(string state) =>
        CircuitTransitions.Add(1, new KeyValuePair<string, object?>("state", state));

    /// <summary>Records a controlled failure category.</summary>
    public static void RecordFailure(ServiceNowFailureKind kind) =>
        Failures.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString().ToLowerInvariant()));
}