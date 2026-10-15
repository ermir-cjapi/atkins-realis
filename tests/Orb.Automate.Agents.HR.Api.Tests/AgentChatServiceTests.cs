using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Contracts;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.Search;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the citation policy shared by complete and streamed grounded responses.
/// </summary>
public sealed class AgentChatServiceTests
{
    private const string ApprovedUrl = "https://contoso.sharepoint.com/sites/hr/policy.aspx";

    /// <summary>
    /// Verifies grounded stream content is replaced before release when it omits an approved citation.
    /// </summary>
    [Fact]
    public void StreamingCitationPolicyRejectsUncitedGroundedResponse()
    {
        var (responseText, citations) = AgentChatService.ApplyCitationPolicy(
            "Employees receive annual leave.",
            CreateGrounding());

        Assert.Equal(
            "I don't have sufficient approved source material to answer that safely. Please raise an HR Case.",
            responseText);
        Assert.Empty(citations);
    }

    /// <summary>
    /// Verifies grounded stream content containing an exact ledger URL passes validation.
    /// </summary>
    [Fact]
    public void StreamingCitationPolicyAcceptsExactApprovedCitation()
    {
        var response = $"Employees receive annual leave. See {ApprovedUrl}.";

        var (responseText, citations) = AgentChatService.ApplyCitationPolicy(response, CreateGrounding());

        Assert.Equal(response, responseText);
        var citation = Assert.Single(citations);
        Assert.Equal(ApprovedUrl, citation.Url);
        Assert.Equal("chunk-1", citation.RetrievalId);
    }

    /// <summary>
    /// Verifies the streaming service emits only the safe fallback when the completed grounded
    /// response does not contain an approved citation.
    /// </summary>
    [Fact]
    public async Task StreamResponseWithholdsUncitedGroundedChunks()
    {
        using var chatClient = new StubChatClient("Employees receive ", "annual leave.");
        var metrics = new RecordingChatAiMetrics();
        var grounding = new GroundingTelemetryContext(metrics);
        using var services = CreateServices(grounding);
        var telemetry = new StubTelemetry();
        var service = new AgentChatService(
            chatClient.AsAIAgent(),
            services,
            new StubTurnContextAccessor(),
            grounding,
            telemetry,
            new StubClassifier(),
            NullLogger<AgentChatService>.Instance,
            new AiTurnTelemetryContext(),
            metrics);
        var updates = new List<Contracts.ChatStreamUpdate>();

        await foreach (var update in service.StreamResponseAsync(CreateTurn("annual leave"), CancellationToken.None))
        {
            updates.Add(update);
        }

        var finalUpdate = Assert.Single(updates);
        Assert.Equal(
            "I don't have sufficient approved source material to answer that safely. Please raise an HR Case.",
            finalUpdate.Text);
        Assert.Equal(["UserMessageReceived", "AgentResponseSent"], telemetry.Events);
        Assert.Equal("safe_fallback", Assert.Single(metrics.Turns).Outcome);
    }

    /// <summary>
    /// Verifies that provider observations made below Agent Framework orchestration, including the
    /// separate classifier call, reach the emitted response event exactly once.
    /// </summary>
    [Fact]
    public async Task DecoratedProviderCallsReachTheEmittedResponseEvent()
    {
        var turnContext = new AiTurnTelemetryContext();
        var metrics = new RecordingChatAiMetrics();
        var grounding = new GroundingTelemetryContext(metrics);
        using var mainInner = new UsageReportingChatClient("model-main", [(30, 10)]);
        using var classifierInner = new UsageReportingChatClient(
            "model-classifier",
            [(8, 2)],
            """{"normalizedIntent":"annual_leave","intentExemplar":"Annual leave entitlement","topic":"Leave of absence","answerConfidence":0.8}""");
        using var mainClient = new DiagnosticChatClient(mainInner, turnContext, metrics, AiComponents.MainAgent);
        using var classifierClient = new DiagnosticChatClient(classifierInner, turnContext, metrics, AiComponents.IntentClassifier);
        var classifier = new FoundryIntentClassifier(
            new IntentClassificationClient(classifierClient),
            Microsoft.Extensions.Options.Options.Create(new TelemetryOptions { TaxonomyVersion = "test-v1" }),
            metrics,
            NullLogger<FoundryIntentClassifier>.Instance);
        var telemetry = new StubTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var service = new AgentChatService(
            mainClient.AsAIAgent(),
            services,
            new StubTurnContextAccessor(),
            grounding,
            telemetry,
            classifier,
            NullLogger<AgentChatService>.Instance,
            turnContext,
            metrics);

        await service.GetResponseAsync(CreateTurn("annual leave"), CancellationToken.None);

        var emitted = Assert.Single(telemetry.Responses);
        Assert.Equal(1, emitted.Ai.MainAgent.CallCount);
        Assert.Equal(30, emitted.Ai.MainAgent.Usage.InputTokens);
        Assert.Equal(10, emitted.Ai.MainAgent.Usage.OutputTokens);
        Assert.Equal(1, emitted.Ai.Classifier.CallCount);
        Assert.Equal(8, emitted.Ai.Classifier.Usage.InputTokens);
        Assert.Equal(38, emitted.Ai.CombinedUsage.InputTokens);
        Assert.Equal(12, emitted.Ai.CombinedUsage.OutputTokens);
        Assert.Equal(30, emitted.PromptTokens);
        Assert.Equal(10, emitted.CompletionTokens);
        Assert.Equal("model-main", emitted.Ai.MainAgent.FinalModelId);
        Assert.Equal("model-classifier", emitted.Ai.Classifier.FinalModelId);
        Assert.True(emitted.Ai.MultipleModelsObserved);
        Assert.True(emitted.Ai.CombinedUsageComplete);
        Assert.Equal("succeeded", emitted.ClassificationOutcome);
        Assert.Equal(2, metrics.ProviderCalls.Count);
        Assert.Equal("success", Assert.Single(metrics.Turns).Outcome);
    }

    /// <summary>Verifies each turn starts from an empty collector rather than inheriting usage.</summary>
    [Fact]
    public async Task ConsecutiveTurnsDoNotAccumulateUsage()
    {
        var turnContext = new AiTurnTelemetryContext();
        var metrics = new RecordingChatAiMetrics();
        var grounding = new GroundingTelemetryContext(metrics);
        using var inner = new UsageReportingChatClient("model-main", [(30, 10)]);
        using var client = new DiagnosticChatClient(inner, turnContext, metrics, AiComponents.MainAgent);
        var telemetry = new StubTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var service = new AgentChatService(
            client.AsAIAgent(),
            services,
            new StubTurnContextAccessor(),
            grounding,
            telemetry,
            new StubClassifier(),
            NullLogger<AgentChatService>.Instance,
            turnContext,
            metrics);

        await service.GetResponseAsync(CreateTurn("first"), CancellationToken.None);
        await service.GetResponseAsync(CreateTurn("second"), CancellationToken.None);

        Assert.Equal(2, telemetry.Responses.Count);
        Assert.All(telemetry.Responses, response =>
        {
            Assert.Equal(1, response.Ai.MainAgent.CallCount);
            Assert.Equal(30, response.Ai.MainAgent.Usage.InputTokens);
        });
    }

    /// <summary>
    /// Verifies an empty grounded stream still emits the insufficient-grounding fallback
    /// instead of producing no updates (an empty chat bubble).
    /// </summary>
    [Fact]
    public async Task StreamResponseEmitsInsufficientGroundingWhenGroundedStreamIsEmpty()
    {
        using var chatClient = new StubChatClient();
        var metrics = new RecordingChatAiMetrics();
        var grounding = new GroundingTelemetryContext(metrics);
        using var services = CreateServices(grounding);
        var telemetry = new StubTelemetry();
        var service = new AgentChatService(
            chatClient.AsAIAgent(),
            services,
            new StubTurnContextAccessor(),
            grounding,
            telemetry,
            new StubClassifier(),
            NullLogger<AgentChatService>.Instance,
            new AiTurnTelemetryContext(),
            metrics);
        var updates = new List<Contracts.ChatStreamUpdate>();

        await foreach (var update in service.StreamResponseAsync(CreateTurn("annual leave"), CancellationToken.None))
        {
            updates.Add(update);
        }

        var finalUpdate = Assert.Single(updates);
        Assert.Equal(
            "I don't have sufficient approved source material to answer that safely. Please raise an HR Case.",
            finalUpdate.Text);
        Assert.Equal("safe_fallback", Assert.Single(metrics.Turns).Outcome);
    }

    /// <summary>
    /// Verifies ungrounded responses stream incrementally instead of being buffered into a
    /// single final event.
    /// </summary>
    [Fact]
    public async Task StreamResponseEmitsIncrementalChunksWhenCitationValidationIsNotRequired()
    {
        using var chatClient = new StubChatClient("Your ", "leave entitlement ", "is 25 days.");
        var metrics = new RecordingChatAiMetrics();
        var grounding = new GroundingTelemetryContext(metrics);
        using var services = new ServiceCollection().BuildServiceProvider();
        var service = new AgentChatService(
            chatClient.AsAIAgent(),
            services,
            new StubTurnContextAccessor(),
            grounding,
            new StubTelemetry(),
            new StubClassifier(),
            NullLogger<AgentChatService>.Instance,
            new AiTurnTelemetryContext(),
            metrics);
        var updates = new List<Contracts.ChatStreamUpdate>();

        await foreach (var update in service.StreamResponseAsync(
            CreateTurn("How much leave do I get?"), CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Equal(3, updates.Count);
        Assert.Equal("Your ", updates[0].Text);
        Assert.Equal("leave entitlement ", updates[1].Text);
        Assert.Equal("is 25 days.", updates[2].Text);
        Assert.All(updates, update => Assert.Null(update.EmailDraft));
    }

    /// <summary>
    /// Verifies provider error content is treated as a failed turn, including after partial text
    /// and when citation validation buffers the response. Responses errors need not throw.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task StreamResponseWithProviderErrorContentRecordsFailure(bool buffered, bool partialText) =>
        AssertFailedProviderStreamAsync(buffered, partialText, throwException: false);

    /// <summary>
    /// Verifies the failure fixture also exercises the existing exception-handling path.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StreamResponseWithThrownProviderErrorRecordsFailure(bool buffered) =>
        AssertFailedProviderStreamAsync(buffered, partialText: true, throwException: true);

    /// <summary>
    /// Verifies a model refusal (refusal error content) completes the turn instead of being
    /// treated as a provider failure.
    /// </summary>
    [Fact]
    public async Task StreamResponseWithRefusalCompletesTurn()
    {
        using var client = new FailingStreamChatClient(partialText: true, throwException: false, errorCode: "Refusal");
        var metrics = new RecordingChatAiMetrics();
        var grounding = new GroundingTelemetryContext(metrics);
        using var services = new ServiceCollection().BuildServiceProvider();
        var telemetry = new StubTelemetry();
        var classifier = new StubClassifier();
        var service = new AgentChatService(
            client.AsAIAgent(), services, new StubTurnContextAccessor(), grounding,
            telemetry, classifier, NullLogger<AgentChatService>.Instance,
            new AiTurnTelemetryContext(), metrics);

        await foreach (var _ in service.StreamResponseAsync(CreateTurn("annual leave"), CancellationToken.None))
        {
        }

        Assert.Equal("success", Assert.Single(metrics.Turns).Outcome);
        Assert.Single(telemetry.Responses);
        Assert.Equal(1, classifier.CallCount);
    }

    private static async Task AssertFailedProviderStreamAsync(bool buffered, bool partialText, bool throwException)
    {
        using var client = new FailingStreamChatClient(partialText, throwException);
        var metrics = new RecordingChatAiMetrics();
        var grounding = new GroundingTelemetryContext(metrics);
        using var services = buffered
            ? CreateServices(grounding)
            : new ServiceCollection().BuildServiceProvider();
        var telemetry = new StubTelemetry();
        var classifier = new StubClassifier();
        var service = new AgentChatService(
            client.AsAIAgent(), services, new StubTurnContextAccessor(), grounding,
            telemetry, classifier, NullLogger<AgentChatService>.Instance,
            new AiTurnTelemetryContext(), metrics);
        var updates = new List<Contracts.ChatStreamUpdate>();

        var exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var update in service.StreamResponseAsync(CreateTurn("annual leave"), CancellationToken.None))
            {
                updates.Add(update);
            }
        });

        Assert.Equal("model_failure", Assert.Single(metrics.Turns).Outcome);
        Assert.NotNull(exception);
        Assert.Empty(telemetry.Responses);
        Assert.Equal(["UserMessageReceived"], telemetry.Events);
        Assert.Equal(0, classifier.CallCount);
        if (buffered || !partialText)
        {
            Assert.Empty(updates);
        }
        else
        {
            Assert.Equal("Partial answer. ", Assert.Single(updates).Text);
        }
    }

    /// <summary>
    /// Verifies that a valid email draft bypasses uncited grounded-response rejection.
    /// </summary>
    [Fact]
    public void ProcessAgentResponse_PreservesEmailDraft_WhenGroundingRequiresCitation()
    {
        const string rawResponse = """
            Here is a draft you can send.

            ```email-draft
            {"subject":"Annual Leave Request","body":"Dear [Name],\n\nKind regards,\n[Your name]","suggestedRecipient":"your HR Business Partner","tone":"professional","policySourceUrls":[]}
            ```
            """;

        var (message, citations, draft, objective) = AgentChatService.ProcessAgentResponse(
            rawResponse,
            CreateGrounding(),
            NullLogger<AgentChatService>.Instance);

        Assert.NotNull(draft);
        Assert.Equal("Annual Leave Request", draft.Subject);
        Assert.Null(objective);
        Assert.Contains("Here is a draft you can send.", message, StringComparison.Ordinal);
        Assert.Empty(citations);
    }

    /// <summary>
    /// Verifies that a SMART objective is always preserved even when grounding requires citations
    /// and the conversational text has none. The text is replaced by the safe fallback message,
    /// but the structured objective payload must still be returned to the client.
    /// </summary>
    [Fact]
    public void ProcessAgentResponse_PreservesSmartObjective_WhenGroundingRequiresCitation()
    {
        const string rawResponse = """
            Here is a SMART objective you can use.

            ```smart-objective
            {"objectiveText":"Reduce average time-to-hire for engineering roles from 45 to 30 days by 31 December 2026.","specific":"Reduce time-to-hire for engineering roles","measurable":"From 45 to 30 days","achievable":"Within scope of the recruiting process improvements already underway","relevant":"Supports the team's hiring priority","timeBound":"By 31 December 2026","policySourceUrls":[]}
            ```
            """;

        var (message, citations, draft, objective) = AgentChatService.ProcessAgentResponse(
            rawResponse,
            CreateGrounding(),
            NullLogger<AgentChatService>.Instance);

        Assert.Null(draft);
        Assert.NotNull(objective);
        Assert.Contains("time-to-hire", objective.ObjectiveText, StringComparison.Ordinal);
        // The conversational text had no citation, so the safe fallback message is used instead —
        // but the objective itself must always be returned.
        Assert.Equal(
            "I don't have sufficient approved source material to answer that safely. Please raise an HR Case.",
            message);
        Assert.Empty(citations);
    }

    private static SearchHrPolicyDocumentsResult CreateGrounding() =>
        new(
            "success",
            "Search succeeded.",
            "annual leave",
            1,
            1,
            [
                new HrPolicyDocumentResult(
                    "chunk-1",
                    "document-1",
                    "Annual Leave",
                    "Approved annual leave guidance.",
                    ApprovedUrl,
                    3.5,
                    "1",
                    null,
                    "UK",
                    "UK",
                    "Employees")
            ]);

    private static ChatTurn CreateTurn(string message) => new(
        message,
        "conversation-1",
        "activity-1",
        1,
        true,
        null,
        new ChatTelemetryContext(
            "conversation-1", "activity-1", "correlation-1", "user@example.com",
            "GB", "HR", "msteams", "test", "1.0.0", "gpt-4o"));

    private static ServiceProvider CreateServices(IGroundingTelemetryContext groundingTelemetry)
    {
        var tool = new SearchHrPolicyDocumentsTool(
            new StubTokenProvider(),
            new StubSearchClient(),
            Microsoft.Extensions.Options.Options.Create(new SharePointSearchOptions()),
            NullLogger<SearchHrPolicyDocumentsTool>.Instance,
            groundingTelemetry);
        return new ServiceCollection()
            .AddSingleton(tool)
            .BuildServiceProvider();
    }

    private sealed class StubTokenProvider : ISearchUserTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-token");
    }

    private sealed class StubSearchClient : ISharePointSearchClient
    {
        public Task<SharePointSearchResponse> SearchAsync(
            string term,
            string? userToken,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SharePointSearchResponse(
                term,
                [
                    new SharePointSearchResult(
                        "chunk-1",
                        "document-1",
                        "Annual Leave",
                        "Approved annual leave guidance.",
                        ApprovedUrl,
                        3.5)
                ]));
    }

    private sealed class StubChatClient(params string[] updates) : IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var text in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FailingStreamChatClient(
        bool partialText, bool throwException, string errorCode = "server_error") : IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (partialText)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Partial answer. ");
            }

            yield return throwException
                ? throw new InvalidOperationException("Simulated provider failure.")
                : new ChatResponseUpdate(ChatRole.Assistant,
                    [new ErrorContent("Simulated provider failure.") { ErrorCode = errorCode }]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubTurnContextAccessor : IServiceNowTurnContextAccessor
    {
        public IDisposable BeginTurn() => NullDisposable.Instance;

        public ServiceNowTurnContext? Current => null;
    }

    private sealed class StubTelemetry : IChatTelemetry
    {
        public List<string> Events { get; } = [];

        public List<AgentResponseTelemetry> Responses { get; } = [];

        public void UserTurnReceived(ChatTurn turn) => Events.Add("UserMessageReceived");

        public void AgentResponseSent(
            ChatTurn turn,
            AgentResponseTelemetry response,
            IReadOnlyList<ChatCitation> citations)
        {
            Events.Add("AgentResponseSent");
            Responses.Add(response);
        }

        public void ConversationClosed(ChatTurn turn)
        {
            if (turn.Close is not null)
            {
                Events.Add("ConversationClosed");
            }
        }
    }

    /// <summary>A provider client that reports a distinct usage payload for each physical call.</summary>
    private sealed class UsageReportingChatClient(
        string modelId,
        (long Input, long Output)[] usage,
        string responseText = "Employees receive annual leave.") : IChatClient
    {
        private int call;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var index = Math.Min(call++, usage.Length - 1);
            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, responseText))
            {
                ModelId = modelId,
                ResponseId = $"{modelId}-{index}",
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails
                {
                    InputTokenCount = usage[index].Input,
                    OutputTokenCount = usage[index].Output,
                    TotalTokenCount = usage[index].Input + usage[index].Output
                }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubClassifier : IIntentClassifier
    {
        public int CallCount { get; private set; }

        public Task<IntentClassificationResult> ClassifyAsync(
            string question,
            string answer,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new IntentClassificationResult(
                new IntentClassification("annual_leave", "Annual leave", "Leave of absence", "1.0", 1),
                IntentClassificationOutcome.Succeeded, "test-model", "classifier-response"));
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
