using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Contracts;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the SMART objective extraction and regenerate prompt injection wired into
/// <see cref="AgentChatService"/>.
/// </summary>
public sealed class AgentChatServiceSmartObjectiveTests
{
    // URL must match the one returned by StubSmartObjectiveSearchClient so that ApplyCitationPolicy
    // finds the citation in the response text (which includes the fence JSON) and passes validation.
    private const string ApprovedUrl = "https://contoso.sharepoint.com/sites/hr/objectives.aspx";

    private const string ObjectiveFencePayload =
        $"{{\"objectiveText\":\"Reduce average time-to-hire for engineering roles from 45 to 30 days by 31 December 2026.\",\"specific\":\"Reduce time-to-hire for engineering roles\",\"measurable\":\"From 45 to 30 days\",\"achievable\":\"Builds on process improvements already underway\",\"relevant\":\"Supports the team's hiring priority\",\"timeBound\":\"By 31 December 2026\",\"policySourceUrls\":[\"{ApprovedUrl}\"]}}";

    private static string BuildFenceResponse(string conversationalPart) =>
        $"{conversationalPart}\n\n```smart-objective\n{ObjectiveFencePayload}\n```";

    /// <summary>
    /// Verifies that the service populates the SmartObjective property of ChatResponse when the
    /// model emits a smart-objective fence block in the completed response.
    /// Uses no grounding so that the basic parsing path is exercised without citation validation.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_PopulatesSmartObjective_WhenModelEmitsFence()
    {
        var modelReply = BuildFenceResponse("Here is a SMART objective you can use.");
        using var chatClient = new CapturingChatClient(modelReply);
        // No grounding — tests the basic fence-parsing path without citation validation.
        var service = CreateService(chatClient);

        var response = await service.GetResponseAsync(
            CreateTurn("Help me write an objective for reducing hiring time."),
            CancellationToken.None);

        Assert.NotNull(response.SmartObjective);
        Assert.Contains("time-to-hire", response.SmartObjective.ObjectiveText, StringComparison.Ordinal);
        Assert.Null(response.EmailDraft);
        Assert.DoesNotContain("```smart-objective", response.Message, StringComparison.Ordinal);
        Assert.Contains("Here is a SMART objective you can use.", response.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that citation validation does not discard the SMART objective when grounding is
    /// present but the conversational text omits ledger URLs. The objective is always returned;
    /// only the conversational text is replaced by the safe fallback message.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_PreservesSmartObjective_WhenGroundedResponseOmitsCitation()
    {
        var modelReply = BuildFenceResponse("Here is a SMART objective you can use.");
        // Grounding is seeded by the client to simulate SearchHrKnowledgeTool firing during the model turn.
        var metrics = new RecordingChatAiMetrics();
        var grounding = new GroundingTelemetryContext(metrics);
        using var chatClient = new CapturingChatClient(modelReply, grounding);
        var service = CreateService(chatClient, groundingTelemetry: grounding, metricsOverride: metrics);

        var response = await service.GetResponseAsync(
            CreateTurn("Yes, please write the objective"),
            CancellationToken.None);

        // Objective is always preserved even when the text is replaced by the fallback.
        Assert.NotNull(response.SmartObjective);
        Assert.Equal(
            "I couldn't find this in the approved HR guidance I have access to, so I don't want to guess. Please [Raise an HR Case](https://atkins.service-now.com/myportal?id=emp_taxonomy_topic&topic_id=b3434b7e1ba64ed0a440ffb7d34bcbc6).",
            response.Message);
    }

    /// <summary>
    /// Verifies that the final stream update carries SmartObjective when the model emits a fence block.
    /// Uses no grounding so that the basic parsing path is exercised without citation validation.
    /// </summary>
    [Fact]
    public async Task StreamResponse_PopulatesSmartObjective_WhenModelEmitsFence()
    {
        var modelReply = BuildFenceResponse("Here is a SMART objective you can use.");
        using var chatClient = new CapturingChatClient(modelReply);
        // No grounding — tests the basic fence-parsing path without citation validation.
        var service = CreateService(chatClient);

        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in service.StreamResponseAsync(
            CreateTurn("Help me write an objective for reducing hiring time."), CancellationToken.None))
        {
            updates.Add(update);
        }

        // Without grounding, the stream is not buffered — text before the fence is emitted
        // as one or more incremental chunks, and the final chunk carries the SmartObjective.
        Assert.NotEmpty(updates);
        var objectiveUpdate = Assert.Single(updates, u => u.SmartObjective is not null);
        Assert.Contains("time-to-hire", objectiveUpdate.SmartObjective!.ObjectiveText, StringComparison.Ordinal);
        Assert.All(updates, u => Assert.DoesNotContain("```smart-objective", u.Text, StringComparison.Ordinal));
        var combinedText = string.Concat(updates.Select(u => u.Text));
        Assert.Contains("Here is a SMART objective you can use.", combinedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the SmartObjective property of ChatStreamUpdate is null when the model emits
    /// plain text.
    /// </summary>
    [Fact]
    public async Task StreamResponse_DoesNotPopulateSmartObjective_WhenModelEmitsPlainText()
    {
        const string plainReply = "You can add objectives in Workday under the Performance Review tile.";
        using var chatClient = new CapturingChatClient(plainReply);
        var service = CreateService(chatClient);

        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in service.StreamResponseAsync(
            CreateTurn("How do I add an objective?"), CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.All(updates, u => Assert.Null(u.SmartObjective));
        Assert.All(updates, u => Assert.DoesNotContain("```smart-objective", u.Text, StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that a regenerate request buffers the stream even without grounding,
    /// and that the fence is stripped from the emitted text.
    /// </summary>
    [Fact]
    public async Task StreamResponse_BuffersAndStripesFence_WhenRegenerateTurn()
    {
        var modelReply = BuildFenceResponse("Here is your revised objective.");
        // No grounding — use a service provider without the SharePoint tool registered.
        using var chatClient = new CapturingChatClient(modelReply);
        var service = CreateService(chatClient);

        var previousObjective = new SmartObjective(
            "Reduce time-to-hire for engineering roles.",
            "Reduce time-to-hire for engineering roles",
            null,
            null,
            "Supports the team's hiring priority",
            null);

        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in service.StreamResponseAsync(
            CreateTurn("Please regenerate.", previousObjective, "Make it more measurable."),
            CancellationToken.None))
        {
            updates.Add(update);
        }

        var singleUpdate = Assert.Single(updates);
        Assert.DoesNotContain("```smart-objective", singleUpdate.Text, StringComparison.Ordinal);
        Assert.Contains("Here is your revised objective.", singleUpdate.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt sent to the LLM contains the [PREVIOUS OBJECTIVE] and
    /// [REGENERATE INSTRUCTION] markers when the request carries a previous objective.
    /// </summary>
    [Fact]
    public async Task StreamResponse_InjectsRegenerateMarkers_WhenPreviousObjectiveIsSet()
    {
        var modelReply = BuildFenceResponse("Revised.");
        using var chatClient = new CapturingChatClient(modelReply);
        var service = CreateService(chatClient);

        var previousObjective = new SmartObjective(
            "Reduce time-to-hire for engineering roles.",
            "Reduce time-to-hire for engineering roles");

        await foreach (var _ in service.StreamResponseAsync(
            CreateTurn("Regenerate please.", previousObjective, "Add a timeframe."),
            CancellationToken.None))
        {
        }

        var capturedPrompt = chatClient.LastPrompt;
        Assert.NotNull(capturedPrompt);
        Assert.Contains("[PREVIOUS OBJECTIVE (client-supplied", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("[REGENERATE INSTRUCTION]", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Add a timeframe.", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Reduce time-to-hire for engineering roles.", capturedPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that no regenerate markers are injected for a normal (non-regenerate) request.
    /// </summary>
    [Fact]
    public async Task StreamResponse_DoesNotInjectRegenerateMarkers_ForNormalRequest()
    {
        using var chatClient = new CapturingChatClient("Some plain answer.");
        var service = CreateService(chatClient);

        await foreach (var _ in service.StreamResponseAsync(
            CreateTurn("How do objectives work?"),
            CancellationToken.None))
        {
        }

        var capturedPrompt = chatClient.LastPrompt;
        Assert.NotNull(capturedPrompt);
        Assert.DoesNotContain("[PREVIOUS OBJECTIVE]", capturedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[REGENERATE INSTRUCTION]", capturedPrompt, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ChatTurn CreateTurn(
        string message,
        SmartObjective? previousObjective = null,
        string? regenerateInstruction = null) => new(
        message,
        "conversation-1",
        "activity-1",
        1,
        true,
        null,
        new ChatTelemetryContext(
            "conversation-1", "activity-1", "correlation-1", "user@example.com",
            "GB", "HR", "unknown", "unknown", "unknown", "msteams", "test", "1.0.0", "gpt-4o"),
        PreviousDraft: null,
        RegenerateInstruction: regenerateInstruction,
        PreviousObjective: previousObjective);

    private static AgentChatService CreateService(
        CapturingChatClient chatClient,
        IGroundingTelemetryContext? groundingTelemetry = null,
        RecordingChatAiMetrics? metricsOverride = null)
    {
        var metrics = metricsOverride ?? new RecordingChatAiMetrics();
        return new(
            chatClient.AsAIAgent(),
            new StubSmartObjectiveTurnContextAccessor(),
            groundingTelemetry ?? new GroundingTelemetryContext(metrics),
            new StubSmartObjectiveTelemetry(),
            new StubSmartObjectiveClassifier(),
            NullLogger<AgentChatService>.Instance,
            new AiTurnTelemetryContext(),
            metrics);
    }

    /// <summary>
    /// Chat client stub that captures the last prompt it receives and returns a fixed reply.
    /// </summary>
    /// <summary>
    /// Chat client stub that captures the last prompt and returns a fixed reply.
    /// Optionally seeds SharePoint grounding telemetry on invocation to simulate
    /// <see cref="SearchHrKnowledgeTool"/> firing during the model turn.
    /// </summary>
    private sealed class CapturingChatClient(
        string reply,
        IGroundingTelemetryContext? groundingTelemetry = null) : IChatClient
    {
        private const string ApprovedUrl = "https://contoso.sharepoint.com/sites/hr/objectives.aspx";

        public string? LastPrompt { get; private set; }

        private void SeedGrounding()
        {
            if (groundingTelemetry is null) { return; }
            groundingTelemetry.RecordSharePoint(
                new SearchHrPolicyDocumentsResult(
                    "success", "Search succeeded.", "objectives", 1, 1,
                    [new HrPolicyDocumentResult(
                        "chunk-1", "document-1", "Performance Objectives", "Approved guidance.",
                        ApprovedUrl, 3.5, "1", null, "UK", "UK", "Employees")]),
                TimeSpan.Zero);
        }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            SeedGrounding();
            LastPrompt = string.Concat(messages.Select(m => m.Text));
            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SeedGrounding();
            LastPrompt = string.Concat(messages.Select(m => m.Text));
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class StubSmartObjectiveTurnContextAccessor : IServiceNowTurnContextAccessor
    {
        public IDisposable BeginTurn() => StubSmartObjectiveDisposable.Instance;
        public ServiceNowTurnContext? Current => null;
    }

    private sealed class StubSmartObjectiveDisposable : IDisposable
    {
        public static readonly StubSmartObjectiveDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class StubSmartObjectiveTelemetry : IChatTelemetry
    {
        public void UserTurnReceived(ChatTurn turn)
        {
        }

        public void AgentResponseSent(
            ChatTurn turn,
            AgentResponseTelemetry response,
            IReadOnlyList<ChatCitation> citations)
        {
        }

        public void ConversationClosed(ChatTurn turn)
        {
        }
    }

    private sealed class StubSmartObjectiveClassifier : IIntentClassifier
    {
        public Task<IntentClassificationResult> ClassifyAsync(
            string question,
            string answer,
            CancellationToken cancellationToken) =>
            Task.FromResult(new IntentClassificationResult(
                new IntentClassification("smart_objective", "Write a SMART objective", "Performance objectives", "1.0", 1),
                IntentClassificationOutcome.Succeeded, "test-model", "classifier-response"));
    }
}
