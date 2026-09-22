using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>
/// Observes every physical provider request without altering its response, its streamed updates or
/// the logical usage reported by the surrounding Agent Framework pipeline.
/// </summary>
/// <param name="innerClient">The provider-facing chat client being decorated.</param>
/// <param name="context">The per-turn provider accounting collector.</param>
/// <param name="metrics">The bounded chat and AI metrics sink.</param>
/// <param name="component">The bounded component name attributed to observed calls.</param>
public sealed class DiagnosticChatClient(
    IChatClient innerClient,
    IAiTurnTelemetryContext context,
    IChatAiMetrics metrics,
    string component) : DelegatingChatClient(innerClient)
{
    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sequence = context.NextSequence();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await InnerClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            Record(sequence, false, response.Usage, response.ModelId, response.ResponseId,
                response.FinishReason?.ToString(), started, null, ProviderCallOutcome.Succeeded);
            return response;
        }
        catch (OperationCanceledException)
        {
            Record(sequence, false, null, null, null, null, started, null, ProviderCallOutcome.Cancelled);
            throw;
        }
        catch
        {
            Record(sequence, false, null, null, null, null, started, null, ProviderCallOutcome.Failed);
            throw;
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sequence = context.NextSequence();
        var started = Stopwatch.GetTimestamp();
        UsageDetails? usage = null;
        string? modelId = null;
        string? responseId = null;
        string? finishReason = null;
        TimeSpan? ttft = null;
        var outcome = ProviderCallOutcome.Abandoned;
        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;

        try
        {
            try
            {
                enumerator = InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                outcome = ProviderCallOutcome.Cancelled;
                throw;
            }
            catch
            {
                outcome = ProviderCallOutcome.Failed;
                throw;
            }
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                    update = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    outcome = ProviderCallOutcome.Cancelled;
                    throw;
                }
                catch
                {
                    outcome = ProviderCallOutcome.Failed;
                    throw;
                }
                modelId = update.ModelId ?? modelId;
                responseId = update.ResponseId ?? responseId;
                finishReason = update.FinishReason?.ToString() ?? finishReason;
                if (ttft is null && !string.IsNullOrEmpty(update.Text))
                {
                    ttft = Stopwatch.GetElapsedTime(started);
                }

                foreach (var content in update.Contents.OfType<UsageContent>())
                {
                    usage ??= new UsageDetails();
                    usage.Add(content.Details);
                }

                yield return update;
            }

            outcome = ProviderCallOutcome.Succeeded;
        }
        finally
        {
            // A failure while disposing the provider enumerator must not replace an in-flight
            // exception, so it is reclassified and swallowed rather than rethrown.
            if (enumerator is not null)
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    outcome = ProviderCallOutcome.Cancelled;
                }
                catch (Exception exception)
                {
                    LogDisposeFailure(exception);
                    outcome = ProviderCallOutcome.Failed;
                }
            }

            Record(sequence, true, usage, modelId, responseId, finishReason, started, ttft, outcome);
        }
    }

    private static void LogDisposeFailure(Exception exception) =>
        Activity.Current?.AddEvent(new ActivityEvent("ai.provider.stream.dispose_failed",
            tags: new ActivityTagsCollection { { "exception.type", exception.GetType().Name } }));

    private void Record(
        long sequence,
        bool streaming,
        UsageDetails? usage,
        string? modelId,
        string? responseId,
        string? finishReason,
        long started,
        TimeSpan? ttft,
        ProviderCallOutcome outcome)
    {
        var observation = new ProviderCallObservation(sequence, component, streaming,
            TokenUsageSnapshot.From(usage), modelId, responseId, finishReason,
            Stopwatch.GetElapsedTime(started), ttft, outcome);
        context.Record(observation);
        metrics.RecordProviderCall(observation);
    }
}
