using Microsoft.Extensions.Logging;
using Orb.Automate.Agents.HR.Api.Contracts;
using Orb.Automate.Agents.HR.Api.Diagnostics;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests the Application Insights custom-event contract.</summary>
public sealed class ChatTelemetryTests
{
    /// <summary>Verifies lifecycle events carry common and event-specific properties without raw text.</summary>
    [Fact]
    public void EmitsExpectedEventNamesAndProperties()
    {
        var logger = new CapturingLogger<ChatTelemetry>();
        var telemetry = new ChatTelemetry(logger);
        var turn = CreateTurn();
        var citation = new ChatCitation(
            "chunk-1", "file-1", "Annual Leave", "https://example.test/policy", 3.5, "1",
            DateTimeOffset.UtcNow, "Sphere", "Sphere > HR Policies", "", "Current");

        telemetry.UserTurnReceived(turn);
        telemetry.AgentResponseSent(turn, CreateResponse(), [citation]);
        telemetry.ConversationClosed(turn);

        Assert.Equal(
            ["ConversationStarted", "UserMessageReceived", "AgentResponseSent", "CitationEmitted", "ConversationClosed"],
            logger.Entries.Select(entry => entry["microsoft.custom_event.name"]));
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal("conversation-1", entry["conversationId"]);
            Assert.Equal("activity-1", entry["activityId"]);
            Assert.Equal("user@example.com", entry["userId"]);
            Assert.Equal("GB", entry["userCountry"]);
            Assert.Equal("HR", entry["userDepartment"]);
            Assert.Equal("Contoso Ltd", entry["userCompany"]);
            Assert.Equal("Corporate Services", entry["userDivision"]);
            Assert.Equal("CC-1001", entry["userCostCenter"]);
        });
        Assert.Equal(1, logger.Entries[1]["messageIndex"]);
        Assert.Equal(1, logger.Entries[2]["citationCount"]);
        Assert.Equal(4, logger.Entries[2]["schemaVersion"]);
        Assert.Equal(1, logger.Entries[2]["messageIndex"]);
        Assert.Equal(0, logger.Entries[2]["historyExchangeCount"]);
        Assert.Equal(0, logger.Entries[2]["historyCharacters"]);
        Assert.Equal("complete", logger.Entries[2]["responseMode"]);
        Assert.Equal(true, logger.Entries[2]["successfulResponse"]);
        Assert.Equal("gpt-4o", logger.Entries[2]["modelDeployment"]);
        Assert.Equal("file-1", logger.Entries[3]["fileId"]);
        Assert.Equal(1, logger.Entries[4]["turnCount"]);
        Assert.DoesNotContain(logger.Entries.SelectMany(entry => entry.Values), value =>
            string.Equals(value?.ToString(), turn.Message, StringComparison.Ordinal));
    }

    /// <summary>Verifies schema-v3 usage, model, grounding and scope fields are emitted with exact values.</summary>
    [Fact]
    public void EmitsSchemaVersionThreeUsageGroundingAndScopeFields()
    {
        var logger = new CapturingLogger<ChatTelemetry>();

        new ChatTelemetry(logger).AgentResponseSent(CreateTurn(), CreateResponse(), []);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(false, entry["outOfScopeRejected"]);
        Assert.Equal(100L, entry["mainInputTokens"]);
        Assert.Equal(25L, entry["mainOutputTokens"]);
        Assert.Equal(8L, entry["classifierInputTokens"]);
        Assert.Equal(108L, entry["combinedInputTokens"]);
        Assert.Equal(27L, entry["combinedOutputTokens"]);
        Assert.Equal(true, entry["combinedUsageComplete"]);
        Assert.Equal(2, entry["mainModelCallCount"]);
        Assert.Equal(1, entry["classifierModelCallCount"]);
        Assert.Equal("gpt-4o-2024-11-20", entry["finalMainModelId"]);
        Assert.Equal(false, entry["multipleModelsObserved"]);
        Assert.Equal("succeeded", entry["classificationOutcome"]);
        Assert.Equal("sharepoint", entry["retrievedSourceSystems"]);
        Assert.Equal(CitationValidationOutcomes.Passed, entry["citationValidationOutcome"]);
        Assert.Equal(0.5d, entry["citationCoverage"]);
        Assert.Equal(40L, entry["groundingDurationMs"]);
        Assert.Equal(180L, entry["modelDurationMs"]);
    }

    /// <summary>Verifies history is described by size only, never by content.</summary>
    [Fact]
    public void EmitsHistorySizeWithoutContent()
    {
        var logger = new CapturingLogger<ChatTelemetry>();
        var turn = CreateTurn() with { History = [new("Can I carry over leave?", "Up to 5 days.")] };

        new ChatTelemetry(logger).AgentResponseSent(turn, CreateResponse(), []);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(1, entry["historyExchangeCount"]);
        Assert.Equal(36, entry["historyCharacters"]);
        Assert.DoesNotContain(entry.Values, value =>
            value?.ToString() is "Can I carry over leave?" or "Up to 5 days.");
    }

    /// <summary>Verifies unreported provider usage stays null rather than becoming zero.</summary>
    [Fact]
    public void EmitsNullTokenFieldsWhenProviderUsageIsUnavailable()
    {
        var logger = new CapturingLogger<ChatTelemetry>();

        new ChatTelemetry(logger).AgentResponseSent(
            CreateTurn(),
            CreateResponse() with
            {
                PromptTokens = null,
                CompletionTokens = null,
                Ai = AiTurnSnapshot.Empty,
                Grounding = GroundingTelemetrySnapshot.Empty,
                OutOfScopeRejected = null
            },
            []);

        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry["outOfScopeRejected"]);
        Assert.Null(entry["promptTokens"]);
        Assert.Null(entry["completionTokens"]);
        Assert.Null(entry["combinedTotalTokens"]);
        Assert.Equal(false, entry["combinedUsageAvailable"]);
        Assert.Null(entry["citationCoverage"]);
    }

    private static AgentResponseTelemetry CreateResponse()
    {
        var main = new AiComponentSnapshot(AiComponents.MainAgent, 2, 2,
            new TokenUsageSnapshot(100, 25, 125, 10, 5), "gpt-4o-2024-11-20", "resp-2", null, ["gpt-4o-2024-11-20"]);
        var classifier = new AiComponentSnapshot(AiComponents.IntentClassifier, 1, 1,
            new TokenUsageSnapshot(8, 2, 10, null, null), "gpt-4o-2024-11-20", "resp-3", null, ["gpt-4o-2024-11-20"]);
        var ai = new AiTurnSnapshot(main, classifier, new TokenUsageSnapshot(108, 27, 135, 10, 5),
            ["gpt-4o-2024-11-20"], false,
        [
            new(1, AiComponents.MainAgent, false, main.Usage, "gpt-4o-2024-11-20", "resp-2", "Stop", TimeSpan.Zero, null, ProviderCallOutcome.Succeeded),
            new(2, AiComponents.IntentClassifier, false, classifier.Usage, "gpt-4o-2024-11-20", "resp-3", "Stop", TimeSpan.Zero, null, ProviderCallOutcome.Succeeded)
        ]);
        var grounding = new GroundingTelemetrySnapshot(
            [new(GroundingTools.SharePointSearch, "orchestrated", "success", "none", TimeSpan.FromMilliseconds(40), 4, 2, 2, "current", "none")],
            4, 2, 2, 1, 0, CitationValidationOutcomes.Passed, 0.5, false, [GroundingSources.SharePoint]);

        return new AgentResponseTelemetry(
            "annual_leave", "Annual leave entitlement", "Leave of absence", "1.0", 0.9,
            "SphereOnly", 1, 250, 100, 25, false, "complete", "success", "resp-2", "succeeded",
            ai, grounding, 40, 180, 30, 250, false, false);
    }

    private static ChatTurn CreateTurn() => new(
        "What is my annual leave entitlement?",
        "conversation-1", "activity-1", 1, true,
        new ConversationClose(30, 1, "UserEnded", true),
        new ChatTelemetryContext(
            "conversation-1", "activity-1", "correlation-1", "user@example.com",
            "GB", "HR", "Contoso Ltd", "Corporate Services", "CC-1001",
            "msteams", "test", "1.0.0", "gpt-4o"));

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<IReadOnlyDictionary<string, object?>> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            Entries.Add(values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
        }
    }
}
