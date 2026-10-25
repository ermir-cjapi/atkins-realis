using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Contracts;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the email draft extraction and regenerate prompt injection wired into
/// <see cref="AgentChatService"/>.
/// </summary>
public sealed class AgentChatServiceEmailDraftTests
{
    // URL must match the one returned by StubEmailDraftSearchClient so that ApplyCitationPolicy
    // finds the citation in the response text (which includes the fence JSON) and passes validation.
    private const string ApprovedUrl = "https://contoso.sharepoint.com/sites/hr/leave.aspx";

    private const string DraftFencePayload =
        $"{{\"subject\":\"Annual Leave Request\",\"body\":\"Dear [Name],\\n\\nI am writing to request additional leave.\\n\\nKind regards,\\n[Your name]\",\"suggestedRecipient\":\"your HR Business Partner\",\"tone\":\"professional\",\"policySourceUrls\":[\"{ApprovedUrl}\"]}}";


    private static string BuildFenceResponse(string conversationalPart) =>
        $"{conversationalPart}\n\n```email-draft\n{DraftFencePayload}\n```";

    /// <summary>
    /// Verifies that the service populates the EmailDraft property of ChatResponse when the model
    /// emits an email-draft fence block in the completed response.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_PopulatesEmailDraft_WhenModelEmitsFence()
    {
        var modelReply = BuildFenceResponse("Here is a draft you can send.");
        using var chatClient = new CapturingChatClient(modelReply);
        var service = CreateService(chatClient);

        var response = await service.GetResponseAsync(
            CreateTurn("I need more annual leave!"),
            CancellationToken.None);

        Assert.NotNull(response.EmailDraft);
        Assert.Equal("Annual Leave Request", response.EmailDraft.Subject);
        Assert.DoesNotContain("```email-draft", response.Message, StringComparison.Ordinal);
        Assert.Contains("Here is a draft you can send.", response.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that citation validation does not replace a valid email draft when grounding
    /// is present but the conversational text omits ledger URLs.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_PreservesEmailDraft_WhenGroundedResponseOmitsCitation()
    {
        var modelReply = BuildFenceResponse("Here is a draft you can send.");
        using var chatClient = new CapturingChatClient(modelReply);
        var service = CreateService(chatClient);

        var response = await service.GetResponseAsync(
            CreateTurn("Yes, please draft an email"),
            CancellationToken.None);

        Assert.NotNull(response.EmailDraft);
        Assert.Contains("Here is a draft you can send.", response.Message, StringComparison.Ordinal);
        Assert.NotEqual(
            "I couldn't find this in the approved HR guidance I have access to, so I don't want to guess. Please [Raise an HR Case](https://atkins.service-now.com/myportal?id=emp_taxonomy_topic&topic_id=b3434b7e1ba64ed0a440ffb7d34bcbc6).",
            response.Message);
    }

    /// <summary>
    /// Verifies that the final stream update carries EmailDraft when the model emits a fence block.
    /// </summary>
    [Fact]
    public async Task StreamResponse_PopulatesEmailDraft_WhenModelEmitsFence()
    {
        var modelReply = BuildFenceResponse("Here is a draft you can send.");
        using var chatClient = new CapturingChatClient(modelReply);
        var service = CreateService(chatClient);

        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in service.StreamResponseAsync(
            CreateTurn("I need more annual leave!"), CancellationToken.None))
        {
            updates.Add(update);
        }

        // In first-turn (non-buffered) mode: pre-fence text is emitted incrementally,
        // then a final update carries the EmailDraft. Assert that the fence never leaks
        // and that the EmailDraft is populated in exactly one of the updates.
        Assert.NotEmpty(updates);
        Assert.All(updates, u => Assert.DoesNotContain("```email-draft", u.Text, StringComparison.Ordinal));
        var combinedText = string.Concat(updates.Select(u => u.Text));
        Assert.Contains("Here is a draft you can send.", combinedText, StringComparison.Ordinal);
        var draftUpdate = Assert.Single(updates, u => u.EmailDraft is not null);
        Assert.Equal("Annual Leave Request", draftUpdate.EmailDraft!.Subject);
    }

    /// <summary>
    /// Verifies that the EmailDraft property of ChatResponse is null when the model emits plain text.
    /// </summary>
    [Fact]
    public async Task StreamResponse_DoesNotPopulateEmailDraft_WhenModelEmitsPlainText()
    {
        const string plainReply = "Your annual leave entitlement is 25 days.";
        using var chatClient = new CapturingChatClient(plainReply);
        var service = CreateService(chatClient);

        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in service.StreamResponseAsync(
            CreateTurn("How much leave do I get?"), CancellationToken.None))
        {
            updates.Add(update);
        }

        // Plain text is returned as emitted (may be un-buffered when no grounding is available,
        // but the absence of an email-draft fence means no EmailDraft regardless).
        Assert.All(updates, u => Assert.DoesNotContain("```email-draft", u.Text, StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that a regenerate request buffers the stream even without grounding,
    /// and that the fence is stripped from the emitted text.
    /// </summary>
    [Fact]
    public async Task StreamResponse_BuffersAndStripesFence_WhenRegenerateTurn()
    {
        var modelReply = BuildFenceResponse("Here is your revised draft.");
        // No grounding — use a service provider without the SharePoint tool registered.
        using var chatClient = new CapturingChatClient(modelReply);
        var service = CreateService(chatClient);

        var previousDraft = new EmailDraft(
            "Original Subject",
            "Dear [Name],\n\nOriginal body.\n\nKind regards,\n[Your name]",
            "your HR Business Partner",
            "professional");

        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in service.StreamResponseAsync(
            CreateTurn("Please regenerate.", previousDraft, "Make it shorter."),
            CancellationToken.None))
        {
            updates.Add(update);
        }

        var singleUpdate = Assert.Single(updates);
        Assert.DoesNotContain("```email-draft", singleUpdate.Text, StringComparison.Ordinal);
        Assert.Contains("Here is your revised draft.", singleUpdate.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt sent to the LLM contains the [PREVIOUS DRAFT] and
    /// [REGENERATE INSTRUCTION] markers when the request carries a previous draft.
    /// </summary>
    [Fact]
    public async Task StreamResponse_InjectsRegenerateMarkers_WhenPreviousDraftIsSet()
    {
        var modelReply = BuildFenceResponse("Revised.");
        using var chatClient = new CapturingChatClient(modelReply);
        var service = CreateService(chatClient);

        var previousDraft = new EmailDraft(
            "Leave Request",
            "Dear [Name],\n\nBody.\n\nKind regards,\n[Your name]",
            "your manager",
            "empathetic");

        await foreach (var _ in service.StreamResponseAsync(
            CreateTurn("Regenerate please.", previousDraft, "Use a formal tone."),
            CancellationToken.None))
        {
        }

        var capturedPrompt = chatClient.LastPrompt;
        Assert.NotNull(capturedPrompt);
        Assert.Contains("[PREVIOUS DRAFT (client-supplied", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("[REGENERATE INSTRUCTION]", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Use a formal tone.", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("Leave Request", capturedPrompt, StringComparison.Ordinal);
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
            CreateTurn("How does sick leave work?"),
            CancellationToken.None))
        {
        }

        var capturedPrompt = chatClient.LastPrompt;
        Assert.NotNull(capturedPrompt);
        Assert.DoesNotContain("[PREVIOUS DRAFT]", capturedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[REGENERATE INSTRUCTION]", capturedPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the prompt contains [PREVIOUS AGENT RESPONSE] markers when AgentContext
    /// is supplied — allowing the model to see its own prior offer on a stateless "yes please" turn.
    /// </summary>
    [Fact]
    public async Task StreamResponse_InjectsAgentContextMarkers_WhenAgentContextIsSet()
    {
        using var chatClient = new CapturingChatClient("Some answer.");
        var service = CreateService(chatClient);

        await foreach (var _ in service.StreamResponseAsync(
            CreateTurn("yes please", agentContext: "I can draft that email for you — would you like me to?"),
            CancellationToken.None))
        {
        }

        var capturedPrompt = chatClient.LastPrompt;
        Assert.NotNull(capturedPrompt);
        Assert.Contains("[PREVIOUS AGENT RESPONSE (client-supplied", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("I can draft that email for you", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("[/PREVIOUS AGENT RESPONSE]", capturedPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that no [PREVIOUS AGENT RESPONSE] markers are injected when AgentContext is absent.
    /// </summary>
    [Fact]
    public async Task StreamResponse_DoesNotInjectAgentContextMarkers_WhenAgentContextIsAbsent()
    {
        using var chatClient = new CapturingChatClient("Some answer.");
        var service = CreateService(chatClient);

        await foreach (var _ in service.StreamResponseAsync(
            CreateTurn("How does sick leave work?"),
            CancellationToken.None))
        {
        }

        var capturedPrompt = chatClient.LastPrompt;
        Assert.NotNull(capturedPrompt);
        Assert.DoesNotContain("[PREVIOUS AGENT RESPONSE]", capturedPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that PolicySourceUrls in the email draft are filtered to only approved ledger
    /// or ServiceNow URLs, stripping any hallucinated links the model may have invented.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_FiltersPolicySourceUrls_RemovingHallucinatedUrls()
    {
        const string hallucinatedUrl = "https://hallucinated.example.com/fake-policy";
        var fenceWithHallucination =
            $"{{\"subject\":\"Leave Request\",\"body\":\"Dear [Name],\\n\\nKind regards,\\n[Your name]\"," +
            $"\"suggestedRecipient\":\"your HR Business Partner\",\"tone\":\"professional\"," +
            $"\"policySourceUrls\":[\"{ApprovedUrl}\",\"{hallucinatedUrl}\"]}}";
        var modelReply = $"Here is a draft.\n\n```email-draft\n{fenceWithHallucination}\n```";
        // Grounding must be seeded so FilterPolicySourceUrls has an approved-URL set to filter against.
        var metrics = new RecordingChatAiMetrics();
        var grounding = new GroundingTelemetryContext(metrics);
        using var chatClient = new CapturingChatClient(grounding, modelReply);
        var service = CreateService(chatClient, groundingTelemetry: grounding, metricsOverride: metrics);

        var response = await service.GetResponseAsync(
            CreateTurn("I need to request annual leave"),
            CancellationToken.None);

        Assert.NotNull(response.EmailDraft);
        Assert.NotNull(response.EmailDraft.PolicySourceUrls);
        Assert.Single(response.EmailDraft.PolicySourceUrls);
        Assert.Equal(ApprovedUrl, response.EmailDraft.PolicySourceUrls[0]);
        Assert.DoesNotContain(hallucinatedUrl, response.EmailDraft.PolicySourceUrls);
    }

    /// <summary>
    /// Verifies a first-draft turn with no_results grounding still withholds the email-draft
    /// fence and populates EmailDraft. Citation validation does not buffer this path, so the
    /// stream must hold back text once a ``` appears.
    /// </summary>
    [Fact]
    public async Task StreamResponse_HoldsBackFenceAndSetsEmailDraft_WhenNoResultsGrounding()
    {
        using var chatClient = new CapturingChatClient(
            "Here is a draft you can send.\n\n",
            "``",
            "`email-draft\n",
            $"{DraftFencePayload}\n",
            "```");
        var service = CreateService(chatClient);

        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in service.StreamResponseAsync(
            CreateTurn("yes please"), CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        Assert.All(updates, update => Assert.DoesNotContain("```", update.Text, StringComparison.Ordinal));
        var combinedText = string.Concat(updates.Select(update => update.Text));
        Assert.Contains("Here is a draft you can send.", combinedText, StringComparison.Ordinal);
        Assert.DoesNotContain("email-draft", combinedText, StringComparison.Ordinal);

        var draftUpdate = Assert.Single(updates, update => update.EmailDraft is not null);
        Assert.Equal("Annual Leave Request", draftUpdate.EmailDraft!.Subject);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ChatTurn CreateTurn(
        string message,
        EmailDraft? previousDraft = null,
        string? regenerateInstruction = null,
        string? agentContext = null) => new(
        message,
        "conversation-1",
        "activity-1",
        1,
        true,
        null,
        new ChatTelemetryContext(
            "conversation-1", "activity-1", "correlation-1", "user@example.com",
            "GB", "HR", "unknown", "unknown", "unknown", "msteams", "test", "1.0.0", "gpt-4o"),
        previousDraft,
        regenerateInstruction,
        PreviousObjective: null,
        agentContext);

    private static AgentChatService CreateService(
        CapturingChatClient chatClient,
        IGroundingTelemetryContext? groundingTelemetry = null,
        RecordingChatAiMetrics? metricsOverride = null)
    {
        var metrics = metricsOverride ?? new RecordingChatAiMetrics();
        return new(
            chatClient.AsAIAgent(),
            new StubEmailDraftTurnContextAccessor(),
            groundingTelemetry ?? new GroundingTelemetryContext(metrics),
            new StubEmailDraftTelemetry(),
            new StubEmailDraftClassifier(),
            NullLogger<AgentChatService>.Instance,
            new AiTurnTelemetryContext(),
            metrics);
    }

    /// <summary>
    /// Chat client stub that captures the last prompt it receives and returns a fixed reply.
    /// Optionally seeds SharePoint grounding telemetry on invocation to simulate
    /// <see cref="SearchHrKnowledgeTool"/> firing during the model turn.
    /// </summary>
    private sealed class CapturingChatClient(
        IGroundingTelemetryContext? groundingTelemetry,
        params string[] replies) : IChatClient
    {
        /// <summary>Convenience constructor when no grounding context is needed.</summary>
        public CapturingChatClient(params string[] replies)
            : this(groundingTelemetry: null, replies) { }

        public string? LastPrompt { get; private set; }

        private void SeedGrounding()
        {
            if (groundingTelemetry is null) { return; }

            groundingTelemetry.RecordSharePoint(
                new SearchHrPolicyDocumentsResult(
                    "success", "Search succeeded.", "leave", 1, 1,
                    [new HrPolicyDocumentResult(
                        "chunk-1", "document-1", "Annual Leave", "Approved annual leave guidance.",
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
                new ChatMessage(ChatRole.Assistant, string.Concat(replies))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SeedGrounding();
            LastPrompt = string.Concat(messages.Select(m => m.Text));
            foreach (var text in replies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class StubEmailDraftTurnContextAccessor : IServiceNowTurnContextAccessor
    {
        public IDisposable BeginTurn() => StubEmailDraftDisposable.Instance;
        public ServiceNowTurnContext? Current => null;
    }

    private sealed class StubEmailDraftDisposable : IDisposable
    {
        public static readonly StubEmailDraftDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class StubEmailDraftTelemetry : IChatTelemetry
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

    private sealed class StubEmailDraftClassifier : IIntentClassifier
    {
        public Task<IntentClassificationResult> ClassifyAsync(
            string question,
            string answer,
            CancellationToken cancellationToken) =>
            Task.FromResult(new IntentClassificationResult(
                new IntentClassification("annual_leave", "Annual leave", "Leave of absence", "1.0", 1),
                IntentClassificationOutcome.Succeeded, "test-model", "classifier-response"));
    }
}
