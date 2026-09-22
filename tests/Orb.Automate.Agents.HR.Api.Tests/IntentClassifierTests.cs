using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests validation and fallback behavior for online intent classification.</summary>
public sealed class IntentClassifierTests
{
    /// <summary>Verifies a valid controlled classification is accepted.</summary>
    [Fact]
    public async Task ClassifyAcceptsValidResponse()
    {
        using var client = new StubChatClient(
            """
            {"normalizedIntent":"annual_leave","intentExemplar":"Annual leave entitlement","topic":"Leave of absence","answerConfidence":0.9}
            """);
        var classifier = CreateClassifier(client);

        var result = await classifier.ClassifyAsync("Question", "Answer", CancellationToken.None);

        Assert.Equal(IntentClassificationOutcome.Succeeded, result.Outcome);
        Assert.Equal("annual_leave", result.Classification.NormalizedIntent);
        Assert.Equal("Annual leave entitlement", result.Classification.IntentExemplar);
        Assert.Equal("Leave of absence", result.Classification.Topic);
        Assert.Equal(0.9, result.Classification.AnswerConfidence);
        Assert.Equal("test-v1", result.Classification.TaxonomyVersion);
        Assert.Equal("classifier-model", result.ModelId);
        Assert.Equal("classifier-response", result.ResponseId);
    }

    /// <summary>Verifies malformed or uncontrolled classifier output fails closed.</summary>
    [Fact]
    public async Task ClassifyUsesUnknownForInvalidResponse()
    {
        using var client = new StubChatClient(
            """
            {"normalizedIntent":"annual_leave","intentExemplar":"Annual leave","topic":"Invented topic","answerConfidence":2}
            """);
        var classifier = CreateClassifier(client);

        var result = await classifier.ClassifyAsync("Question", "Answer", CancellationToken.None);

        Assert.Equal(IntentClassificationOutcome.InvalidResponse, result.Outcome);
        Assert.Equal(IntentClassification.Unknown("test-v1"), result.Classification);
    }

    /// <summary>Verifies cosmetic label variation is normalised rather than failing closed.</summary>
    [Theory]
    [InlineData("Annual-Leave", "annual_leave")]
    [InlineData("annual leave", "annual_leave")]
    [InlineData("__annual__leave__", "annual_leave")]
    public async Task ClassifyNormalizesIntentLabels(string returned, string expected)
    {
        using var client = new StubChatClient(
            $$"""
            {"normalizedIntent":"{{returned}}","intentExemplar":"Annual leave entitlement","topic":"Leave of absence","answerConfidence":0.9}
            """);

        var result = await CreateClassifier(client).ClassifyAsync("Question", "Answer", CancellationToken.None);

        Assert.Equal(IntentClassificationOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Classification.NormalizedIntent);
    }

    /// <summary>Verifies exemplars copied from the interaction fail closed and never reach telemetry.</summary>
    [Theory]
    [InlineData("How much leave does Alice Smith get?")]
    [InlineData("Alice Smith receives 25 days.")]
    public async Task ClassifyRejectsExemplarsCopiedFromTheInteraction(string exemplar)
    {
        using var client = new StubChatClient(
            $$"""
            {"normalizedIntent":"annual_leave","intentExemplar":"{{exemplar}}","topic":"Leave of absence","answerConfidence":0.9}
            """);

        var result = await CreateClassifier(client).ClassifyAsync(
            "How much leave does Alice Smith get?", "Alice Smith receives 25 days.", CancellationToken.None);

        Assert.Equal(IntentClassificationOutcome.InvalidResponse, result.Outcome);
        Assert.Equal(IntentClassification.Unknown("test-v1"), result.Classification);
    }

    /// <summary>Verifies oversized classifier values fail closed instead of entering telemetry.</summary>
    [Fact]
    public async Task ClassifyRejectsOversizedValues()
    {
        using var client = new StubChatClient(
            $$"""
            {"normalizedIntent":"annual_leave","intentExemplar":"{{new string('a', 200)}}","topic":"Leave of absence","answerConfidence":0.9}
            """);

        var result = await CreateClassifier(client).ClassifyAsync("Question", "Answer", CancellationToken.None);

        Assert.Equal(IntentClassificationOutcome.InvalidResponse, result.Outcome);
    }

    /// <summary>Verifies provider failures fail closed while caller cancellation is preserved.</summary>
    [Fact]
    public async Task ProviderFailureUsesUnknownAndCancellationPropagates()
    {
        using var failedClient = new ThrowingChatClient(new InvalidOperationException("not emitted"));
        var failed = await CreateClassifier(failedClient).ClassifyAsync("Question", "Answer", CancellationToken.None);
        Assert.Equal(IntentClassificationOutcome.Failed, failed.Outcome);
        Assert.Equal("unknown", failed.Classification.NormalizedIntent);

        using var cancelledClient = new ThrowingChatClient(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClassifier(cancelledClient).ClassifyAsync("Question", "Answer", CancellationToken.None));
    }

    private static FoundryIntentClassifier CreateClassifier(IChatClient client) => new(
        new IntentClassificationClient(client),
        Microsoft.Extensions.Options.Options.Create(new TelemetryOptions { TaxonomyVersion = "test-v1" }),
        new RecordingChatAiMetrics(),
        NullLogger<FoundryIntentClassifier>.Instance);

    private sealed class StubChatClient(string response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response))
            {
                ModelId = "classifier-model",
                ResponseId = "classifier-response"
            });

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

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromException<ChatResponse>(exception);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
