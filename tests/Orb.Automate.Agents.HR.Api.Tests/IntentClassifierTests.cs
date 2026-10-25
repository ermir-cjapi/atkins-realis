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
            {"normalizedIntent":"annual_leave","intentExemplar":"Annual leave entitlement","topic":"Leave of absence","answerConfidence":0.9,"outOfScopeRejected":false}
            """);
        var classifier = CreateClassifier(client);

        var result = await classifier.ClassifyAsync("Question", "Answer", CancellationToken.None);

        Assert.Equal(IntentClassificationOutcome.Succeeded, result.Outcome);
        Assert.Equal("annual_leave", result.Classification.NormalizedIntent);
        Assert.Equal("Annual leave entitlement", result.Classification.IntentExemplar);
        Assert.Equal("Leave of absence", result.Classification.Topic);
        Assert.Equal(0.9, result.Classification.AnswerConfidence);
        Assert.False(result.Classification.OutOfScopeRejected);
        Assert.Equal("test-v1", result.Classification.TaxonomyVersion);
        Assert.Equal("classifier-model", result.ModelId);
        Assert.Equal("classifier-response", result.ResponseId);
    }

    /// <summary>Verifies malformed or uncontrolled classifier output fails closed.</summary>
    [Theory]
    [InlineData("Invented topic", 0.9)]
    [InlineData("Leave of absence", -0.1)]
    [InlineData("Leave of absence", 1.1)]
    public async Task ClassifyUsesUnknownForInvalidResponse(string topic, double confidence)
    {
        using var client = new StubChatClient(
            $$"""
            {"normalizedIntent":"annual_leave","intentExemplar":"Annual leave","topic":"{{topic}}","answerConfidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"outOfScopeRejected":false}
            """);
        var classifier = CreateClassifier(client);

        var result = await classifier.ClassifyAsync("Question", "Answer", CancellationToken.None);

        Assert.Equal(IntentClassificationOutcome.InvalidResponse, result.Outcome);
        Assert.Equal(IntentClassification.Unknown("test-v1"), result.Classification);
    }

    /// <summary>Verifies an out-of-scope rejection under the Other topic is accepted.</summary>
    [Fact]
    public async Task ClassifyAcceptsOutOfScopeRejectionWithOtherTopic()
    {
        using var client = new StubChatClient(
            """
            {"normalizedIntent":"office_wifi","intentExemplar":"Office wifi access","topic":"Other","answerConfidence":0.9,"outOfScopeRejected":true}
            """);

        var result = await CreateClassifier(client).ClassifyAsync("Question", "Answer", CancellationToken.None);

        Assert.Equal(IntentClassificationOutcome.Succeeded, result.Outcome);
        Assert.Equal("Other", result.Classification.Topic);
        Assert.True(result.Classification.OutOfScopeRejected);
    }

    /// <summary>Verifies a missing or non-boolean out-of-scope flag fails closed.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(",\"outOfScopeRejected\":null")]
    [InlineData(",\"outOfScopeRejected\":\"yes\"")]
    public async Task ClassifyRejectsMissingOrInvalidOutOfScopeFlag(string flag)
    {
        using var client = new StubChatClient(
            $$"""
            {"normalizedIntent":"annual_leave","intentExemplar":"Annual leave entitlement","topic":"Leave of absence","answerConfidence":0.9{{flag}}}
            """);

        var result = await CreateClassifier(client).ClassifyAsync("Question", "Answer", CancellationToken.None);

        Assert.Equal(IntentClassificationOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Classification.OutOfScopeRejected);
        Assert.Equal("classifier-model", result.ModelId);
        Assert.Equal("classifier-response", result.ResponseId);
    }

    /// <summary>Verifies malformed model output is not counted as a provider failure.</summary>
    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"outOfScopeRejected\":")]
    [InlineData("{\"outOfScopeRejected\":1}")]
    public async Task MalformedOutputRecordsInvalidResponse(string output)
    {
        using var client = new StubChatClient(output);
        var metrics = new RecordingChatAiMetrics();
        var classifier = CreateClassifier(client, metrics);

        var result = await classifier.ClassifyAsync("Question", "Answer", CancellationToken.None);

        Assert.Equal(IntentClassificationOutcome.InvalidResponse, result.Outcome);
        Assert.Equal(IntentClassification.Unknown("test-v1"), result.Classification);
        Assert.Equal("classifier-model", result.ModelId);
        Assert.Equal("classifier-response", result.ResponseId);
        Assert.Equal("invalid_response", Assert.Single(metrics.Classifications));
    }

    /// <summary>Verifies the classifier receives guidance for scope and non-scope refusals.</summary>
    [Theory]
    [InlineData("Your specific grievance case is outside my scope; raise an HR Case.\" => true")]
    [InlineData("I cannot look up individual leave balances; that is outside my scope.\" => true")]
    [InlineData("I cannot approve your leave extension, but I can draft an email requesting it.\" => false")]
    [InlineData("I could not verify approved guidance. Please raise an HR Case.\" => false")]
    [InlineData("Here is the leave policy. For further help, raise an HR Case.\" => false")]
    public async Task PromptIncludesScopeRejectionGuidance(string guidance)
    {
        using var client = new StubChatClient("{}");

        await CreateClassifier(client).ClassifyAsync("Question", "Answer", CancellationToken.None);

        Assert.Contains(guidance, client.LastPrompt);
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
            {"normalizedIntent":"{{returned}}","intentExemplar":"Annual leave entitlement","topic":"Leave of absence","answerConfidence":0.9,"outOfScopeRejected":false}
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
            {"normalizedIntent":"annual_leave","intentExemplar":"{{exemplar}}","topic":"Leave of absence","answerConfidence":0.9,"outOfScopeRejected":false}
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
            {"normalizedIntent":"annual_leave","intentExemplar":"{{new string('a', 200)}}","topic":"Leave of absence","answerConfidence":0.9,"outOfScopeRejected":false}
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
        Assert.Null(failed.Classification.OutOfScopeRejected);

        using var cancelledClient = new ThrowingChatClient(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClassifier(cancelledClient).ClassifyAsync("Question", "Answer", CancellationToken.None));
    }

    private static FoundryIntentClassifier CreateClassifier(
        IChatClient client, RecordingChatAiMetrics? metrics = null) => new(
        new IntentClassificationClient(client),
        Microsoft.Extensions.Options.Options.Create(new TelemetryOptions { TaxonomyVersion = "test-v1" }),
        metrics ?? new RecordingChatAiMetrics(),
        NullLogger<FoundryIntentClassifier>.Instance);

    private sealed class StubChatClient(string response) : IChatClient
    {
        public string? LastPrompt { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastPrompt = string.Join("\n", messages.Select(message => message.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response))
            {
                ModelId = "classifier-model",
                ResponseId = "classifier-response"
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
