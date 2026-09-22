using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Orb.Automate.Agents.HR.Api.Diagnostics;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests physical provider-call observation for complete and streaming requests.</summary>
public sealed class DiagnosticChatClientTests
{
    /// <summary>Verifies every nullable usage field and the provider metadata are captured.</summary>
    [Fact]
    public async Task CompleteResponseCapturesEveryUsageFieldAndMetadata()
    {
        var context = new AiTurnTelemetryContext();
        var metrics = new RecordingChatAiMetrics();
        using var scope = context.BeginTurn();
        using var client = Create(new StubClient
        {
            Response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 4,
                    TotalTokenCount = 14,
                    CachedInputTokenCount = 3,
                    ReasoningTokenCount = 2
                },
                ModelId = "actual-a",
                ResponseId = "response-1",
                FinishReason = ChatFinishReason.Stop
            }
        }, context, metrics, AiComponents.MainAgent);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        var call = Assert.Single(context.Snapshot.Calls);
        Assert.Equal(new TokenUsageSnapshot(10, 4, 14, 3, 2), call.Usage);
        Assert.Equal("actual-a", call.ModelId);
        Assert.Equal("response-1", call.ResponseId);
        Assert.Equal("stop", call.FinishReason);
        Assert.False(call.Streaming);
        Assert.Equal(ProviderCallOutcome.Succeeded, call.Outcome);
        Assert.Same(call, Assert.Single(metrics.ProviderCalls));
    }

    /// <summary>Verifies absent provider usage is never converted to zero.</summary>
    [Fact]
    public async Task MissingUsageStaysUnavailable()
    {
        var context = new AiTurnTelemetryContext();
        using var scope = context.BeginTurn();
        using var client = Create(new StubClient(), context, new RecordingChatAiMetrics(), AiComponents.MainAgent);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        var snapshot = context.Snapshot;
        Assert.False(snapshot.MainAgent.Usage.Available);
        Assert.Null(snapshot.CombinedUsage.TotalTokens);
        Assert.False(snapshot.MainAgent.UsageComplete);
        Assert.False(snapshot.CombinedUsageComplete);
    }

    /// <summary>Verifies tool-loop rounds and the classifier call aggregate exactly once each.</summary>
    [Fact]
    public async Task MainAndClassifierCallsAggregateExactlyOnceAndRetainModels()
    {
        var context = new AiTurnTelemetryContext();
        var metrics = new RecordingChatAiMetrics();
        using var scope = context.BeginTurn();
        using var main = Create(new StubClient { Input = 5, Output = 2, Model = "model-a" }, context, metrics, AiComponents.MainAgent);
        using var classifier = Create(new StubClient { Input = 3, Output = 1, Model = "model-b" }, context, metrics, AiComponents.IntentClassifier);

        await main.GetResponseAsync([new ChatMessage(ChatRole.User, "one")]);
        await main.GetResponseAsync([new ChatMessage(ChatRole.User, "two")]);
        await classifier.GetResponseAsync([new ChatMessage(ChatRole.User, "three")]);

        var snapshot = context.Snapshot;
        Assert.Equal(2, snapshot.MainAgent.CallCount);
        Assert.Equal(1, snapshot.Classifier.CallCount);
        Assert.Equal(10, snapshot.MainAgent.Usage.InputTokens);
        Assert.Equal(3, snapshot.Classifier.Usage.InputTokens);
        Assert.Equal(13, snapshot.CombinedUsage.InputTokens);
        Assert.Equal(5, snapshot.CombinedUsage.OutputTokens);
        Assert.True(snapshot.CombinedUsageComplete);
        Assert.True(snapshot.MultipleModelsObserved);
        Assert.Equal(["model-a", "model-b"], snapshot.DistinctModelIds);
        Assert.Equal(3, metrics.ProviderCalls.Count);
    }

    /// <summary>
    /// Verifies a call without usage keeps the reported totals but marks the component incomplete
    /// rather than silently publishing a partial figure as exact.
    /// </summary>
    [Fact]
    public async Task PartialUsageIsReportedWithAnIncompleteFlag()
    {
        var context = new AiTurnTelemetryContext();
        var metrics = new RecordingChatAiMetrics();
        using var scope = context.BeginTurn();
        using var reported = Create(new StubClient { Input = 5 }, context, metrics, AiComponents.MainAgent);
        using var missing = Create(new StubClient(), context, metrics, AiComponents.MainAgent);

        await reported.GetResponseAsync([]);
        await missing.GetResponseAsync([]);

        var snapshot = context.Snapshot;
        Assert.Equal(5, snapshot.MainAgent.Usage.InputTokens);
        Assert.Equal(2, snapshot.MainAgent.CallCount);
        Assert.Equal(1, snapshot.MainAgent.UsageReportingCallCount);
        Assert.False(snapshot.MainAgent.UsageComplete);
        Assert.False(snapshot.CombinedUsageComplete);
    }

    /// <summary>Verifies usage-only updates and late model metadata are both retained.</summary>
    [Fact]
    public async Task StreamingAggregatesUsageOnlyUpdatesAndLateMetadata()
    {
        var context = new AiTurnTelemetryContext();
        using var scope = context.BeginTurn();
        using var client = Create(new StubClient { Stream = true }, context, new RecordingChatAiMetrics(), AiComponents.MainAgent);

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")]))
        {
        }

        var call = Assert.Single(context.Snapshot.Calls);
        Assert.Equal(7, call.Usage.InputTokens);
        Assert.Equal(3, call.Usage.OutputTokens);
        Assert.Equal("late-model", call.ModelId);
        Assert.Equal("late-response", call.ResponseId);
        Assert.True(call.Streaming);
        Assert.NotNull(call.TimeToFirstText);
    }

    /// <summary>Verifies abandoning enumeration preserves the usage already observed.</summary>
    [Fact]
    public async Task EarlyDisposalRecordsPartialUsageAsAbandoned()
    {
        var context = new AiTurnTelemetryContext();
        using var scope = context.BeginTurn();
        using var client = Create(new StubClient { Stream = true }, context, new RecordingChatAiMetrics(), AiComponents.MainAgent);
        await using (var enumerator = client
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")])
            .GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.True(await enumerator.MoveNextAsync());
        }

        var call = Assert.Single(context.Snapshot.Calls);
        Assert.Equal(ProviderCallOutcome.Abandoned, call.Outcome);
        Assert.Equal(7, call.Usage.InputTokens);
    }

    /// <summary>Verifies cancellation keeps partial usage and still propagates to the caller.</summary>
    [Fact]
    public async Task StreamingCancellationPreservesPartialUsageAndPropagates()
    {
        var context = new AiTurnTelemetryContext();
        using var scope = context.BeginTurn();
        using var client = Create(new StubClient { Stream = true, FailAfterUsage = new OperationCanceledException() },
            context, new RecordingChatAiMetrics(), AiComponents.MainAgent);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")]))
            {
            }
        });

        var call = Assert.Single(context.Snapshot.Calls);
        Assert.Equal(ProviderCallOutcome.Cancelled, call.Outcome);
        Assert.Equal(7, call.Usage.InputTokens);
    }

    /// <summary>Verifies streaming failures are classified and rethrown unchanged.</summary>
    [Fact]
    public async Task StreamingFailurePropagatesOriginalException()
    {
        var context = new AiTurnTelemetryContext();
        using var scope = context.BeginTurn();
        using var client = Create(new StubClient { Stream = true, FailAfterUsage = new InvalidOperationException("provider") },
            context, new RecordingChatAiMetrics(), AiComponents.MainAgent);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")]))
            {
            }
        });

        Assert.Equal("provider", exception.Message);
        Assert.Equal(ProviderCallOutcome.Failed, Assert.Single(context.Snapshot.Calls).Outcome);
    }

    /// <summary>Verifies a dispose failure does not replace the in-flight provider exception.</summary>
    [Fact]
    public async Task DisposeFailureDoesNotMaskTheOriginalException()
    {
        var context = new AiTurnTelemetryContext();
        using var scope = context.BeginTurn();
        using var client = Create(
            new StubClient { Stream = true, FailAfterUsage = new InvalidOperationException("provider"), FailOnDispose = true },
            context, new RecordingChatAiMetrics(), AiComponents.MainAgent);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")]))
            {
            }
        });

        Assert.Equal("provider", exception.Message);
    }

    /// <summary>Verifies complete-call failure and cancellation are classified and rethrown.</summary>
    [Fact]
    public async Task ProviderFailureAndCancellationPropagate()
    {
        var context = new AiTurnTelemetryContext();
        using var scope = context.BeginTurn();
        using var failed = Create(new StubClient { Exception = new InvalidOperationException() },
            context, new RecordingChatAiMetrics(), AiComponents.MainAgent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.GetResponseAsync([]));

        using var cancelled = Create(new StubClient { Exception = new OperationCanceledException() },
            context, new RecordingChatAiMetrics(), AiComponents.MainAgent);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.GetResponseAsync([]));

        Assert.Equal(
            [ProviderCallOutcome.Failed, ProviderCallOutcome.Cancelled],
            context.Snapshot.Calls.Select(call => call.Outcome));
    }

    private static DiagnosticChatClient Create(
        IChatClient inner,
        IAiTurnTelemetryContext context,
        IChatAiMetrics metrics,
        string component) => new(inner, context, metrics, component);

    private sealed class StubClient : IChatClient
    {
        public ChatResponse Response { get; init; } = new(new ChatMessage(ChatRole.Assistant, "ok"));

        public long? Input { get; init; }

        public long? Output { get; init; }

        public string? Model { get; init; }

        public bool Stream { get; init; }

        public Exception? FailAfterUsage { get; init; }

        public bool FailOnDispose { get; init; }

        public Exception? Exception { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Exception is not null
            ? Task.FromException<ChatResponse>(Exception)
            : Input.HasValue || Output.HasValue || Model is not null
                ? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                {
                    Usage = new UsageDetails { InputTokenCount = Input, OutputTokenCount = Output },
                    ModelId = Model
                })
                : Task.FromResult(Response);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            new StubStream(this, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubStream(StubClient client, CancellationToken cancellationToken) : IAsyncEnumerable<ChatResponseUpdate>
    {
        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken token = default) =>
            new Enumerator(client, Enumerate(client, cancellationToken).GetAsyncEnumerator(token));

        private static async IAsyncEnumerable<ChatResponseUpdate> Enumerate(
            StubClient client,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (!client.Stream)
            {
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello");
            yield return new ChatResponseUpdate
            {
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 })]
            };

            ThrowIfSet(client.FailAfterUsage);
            yield return new ChatResponseUpdate { ModelId = "late-model", ResponseId = "late-response" };
        }

        private static void ThrowIfSet(Exception? exception)
        {
            if (exception is not null)
            {
                throw exception;
            }
        }

        private sealed class Enumerator(StubClient client, IAsyncEnumerator<ChatResponseUpdate> inner)
            : IAsyncEnumerator<ChatResponseUpdate>
        {
            public ChatResponseUpdate Current => inner.Current;

            public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();

            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (client.FailOnDispose)
                {
                    throw new InvalidOperationException("dispose");
                }
            }
        }
    }
}
