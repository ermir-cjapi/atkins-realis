using System.Diagnostics.Metrics;
using Orb.Automate.Agents.HR.Api.Diagnostics;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Verifies emitted metric values and that every tag stays bounded and content-free.</summary>
public sealed class ChatAiMetricsTests
{
    private const string SecretModelId = "gpt-4o-2024-11-20-deployment-42";
    private const string SecretResponseId = "resp_01JABCDEF";

    private static readonly string[] AllowedTagNames =
    [
        "component", "streaming_mode", "outcome", "configured_deployment", "grounding_source",
        "result_type", "tool", "invocation_mode", "failure_kind", "token_type"
    ];

    /// <summary>Verifies provider counters and histograms record exact values with bounded tags.</summary>
    [Fact]
    public void ProviderCallEmitsExactValuesWithBoundedTags()
    {
        using var listener = new MetricCollector();
        var metrics = new ChatAiMetrics();

        metrics.RecordProviderCall(new ProviderCallObservation(
            1, AiComponents.MainAgent, true, new TokenUsageSnapshot(13, 5, 18, 2, 1),
            SecretModelId, SecretResponseId, "stop", TimeSpan.FromMilliseconds(9),
            TimeSpan.FromMilliseconds(4), ProviderCallOutcome.Succeeded));

        var measurements = listener.Measurements;
        var call = Assert.Single(measurements, item => item.Name == "ai.provider.call.count");
        Assert.Equal(1d, call.Value);
        Assert.Equal("main_agent", call.Tags["component"]);
        Assert.Equal("streaming", call.Tags["streaming_mode"]);
        Assert.Equal("succeeded", call.Tags["outcome"]);
        Assert.Equal(9d, Assert.Single(measurements, item => item.Name == "ai.provider.call.duration").Value);
        Assert.Equal(4d, Assert.Single(measurements, item => item.Name == "ai.provider.ttft").Value);
        Assert.Equal(
            [13d, 5d, 18d, 2d, 1d],
            measurements.Where(item => item.Name == "ai.provider.token.count").Select(item => item.Value));
        listener.AssertTagsAreBounded(AllowedTagNames);
        listener.AssertNoValueContains(SecretModelId, SecretResponseId);
    }

    /// <summary>
    /// Verifies the turn token series publishes only per-component values, so summing it without a
    /// component filter cannot double count the combined total.
    /// </summary>
    [Fact]
    public void TurnTokenSeriesPublishesComponentsOnly()
    {
        using var listener = new MetricCollector();
        var metrics = new ChatAiMetrics();
        var snapshot = new AiTurnSnapshot(
            new AiComponentSnapshot(AiComponents.MainAgent, 2, 2, new TokenUsageSnapshot(10, 4, null, null, null), null, null, null, []),
            new AiComponentSnapshot(AiComponents.IntentClassifier, 1, 1, new TokenUsageSnapshot(3, 1, null, null, null), null, null, null, []),
            new TokenUsageSnapshot(13, 5, null, null, null),
            [],
            false,
            []);

        metrics.RecordTurn(TimeSpan.FromMilliseconds(120), "complete", "success", "gpt-4o", snapshot);

        var tokens = listener.Measurements.Where(item => item.Name == "chat.turn.token.count").ToArray();
        Assert.DoesNotContain(tokens, item => Equals(item.Tags["component"], "combined"));
        Assert.Equal(13d, tokens.Where(item => Equals(item.Tags["token_type"], "input")).Sum(item => item.Value));
        Assert.Equal(5d, tokens.Where(item => Equals(item.Tags["token_type"], "output")).Sum(item => item.Value));
        var turn = Assert.Single(listener.Measurements, item => item.Name == "chat.turn.count");
        Assert.Equal("gpt-4o", turn.Tags["configured_deployment"]);
        Assert.Equal(120d, Assert.Single(listener.Measurements, item => item.Name == "chat.turn.duration").Value);
        listener.AssertTagsAreBounded(AllowedTagNames);
    }

    /// <summary>Verifies grounding, tool, citation and classification metrics stay bounded.</summary>
    [Fact]
    public void GroundingToolAndCitationMetricsStayBounded()
    {
        using var listener = new MetricCollector();
        var metrics = new ChatAiMetrics();

        metrics.RecordGrounding(TimeSpan.FromMilliseconds(30), GroundingSources.SharePoint, 7, 3);
        metrics.RecordTool(GroundingTools.SharePointSearch, "orchestrated", "succeeded", "none", TimeSpan.FromMilliseconds(25));
        metrics.RecordCitation(CitationValidationOutcomes.Passed, 0.5);
        metrics.RecordClassification("succeeded");

        var results = listener.Measurements.Where(item => item.Name == "chat.grounding.result.count").ToArray();
        Assert.Equal(7d, Assert.Single(results, item => Equals(item.Tags["result_type"], "retrieved")).Value);
        Assert.Equal(3d, Assert.Single(results, item => Equals(item.Tags["result_type"], "returned")).Value);
        Assert.Equal(25d, Assert.Single(listener.Measurements, item => item.Name == "chat.tool.duration").Value);
        Assert.Equal(0.5d, Assert.Single(listener.Measurements, item => item.Name == "chat.citation.coverage").Value);
        Assert.Equal(1d, Assert.Single(listener.Measurements, item => item.Name == "chat.classification.count").Value);
        listener.AssertTagsAreBounded(AllowedTagNames);
    }

    /// <summary>Verifies undefined citation coverage is omitted rather than reported as zero.</summary>
    [Fact]
    public void UndefinedCitationCoverageIsNotRecorded()
    {
        using var listener = new MetricCollector();

        new ChatAiMetrics().RecordCitation(CitationValidationOutcomes.NotRequired, null);

        Assert.DoesNotContain(listener.Measurements, item => item.Name == "chat.citation.coverage");
    }

    private sealed record Measurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>Collects measurements published by this test's own metric instances only.</summary>
    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly List<Measurement> measurements = [];
        private readonly Lock gate = new();

        public MetricCollector()
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ChatAiMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
            listener.Start();
        }

        public IReadOnlyList<Measurement> Measurements
        {
            get
            {
                lock (gate)
                {
                    return [.. measurements];
                }
            }
        }

        public void AssertTagsAreBounded(IReadOnlyCollection<string> allowedNames) =>
            Assert.All(Measurements, measurement => Assert.All(measurement.Tags, tag =>
            {
                Assert.Contains(tag.Key, allowedNames);
                Assert.IsType<string>(tag.Value);
                Assert.Matches("^[a-z0-9_.-]+$", (string)tag.Value!);
            }));

        public void AssertNoValueContains(params string[] forbidden) =>
            Assert.All(Measurements, measurement => Assert.All(measurement.Tags.Values, value =>
                Assert.DoesNotContain(value?.ToString(), forbidden)));

        public void Dispose() => listener.Dispose();

        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var captured = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);
            lock (gate)
            {
                measurements.Add(new Measurement(name, value, captured));
            }
        }
    }
}
