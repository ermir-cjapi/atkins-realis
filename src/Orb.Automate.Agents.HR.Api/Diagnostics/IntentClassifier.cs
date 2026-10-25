using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>A controlled classification attached to a completed agent answer.</summary>
/// <param name="NormalizedIntent">The stable snake_case intent label.</param>
/// <param name="IntentExemplar">The short canonical question for the intent.</param>
/// <param name="Topic">A topic from the controlled taxonomy.</param>
/// <param name="TaxonomyVersion">The configured taxonomy version.</param>
/// <param name="AnswerConfidence">The classifier confidence between zero and one.</param>
/// <param name="OutOfScopeRejected">
/// Whether the answer told the user that all or part of the request is out of scope, or null when
/// classification did not succeed.
/// </param>
public sealed record IntentClassification(
    string NormalizedIntent,
    string IntentExemplar,
    string Topic,
    string TaxonomyVersion,
    double AnswerConfidence,
    bool? OutOfScopeRejected = null)
{
    /// <summary>Creates the fail-closed classification.</summary>
    /// <param name="taxonomyVersion">The configured taxonomy version.</param>
    /// <returns>The controlled unknown classification.</returns>
    public static IntentClassification Unknown(string taxonomyVersion) =>
        new("unknown", "Unknown", "Unknown", taxonomyVersion, 0);
}

/// <summary>Controlled semantic result of the classification operation.</summary>
public enum IntentClassificationOutcome
{
    /// <summary>The classifier returned a usable controlled classification.</summary>
    Succeeded,

    /// <summary>The classifier responded but its output failed validation.</summary>
    InvalidResponse,

    /// <summary>The classification call failed.</summary>
    Failed
}

/// <summary>
/// Classification plus provider metadata. Token usage is deliberately absent because it is captured
/// only by the decorated classifier client.
/// </summary>
/// <param name="Classification">The controlled classification.</param>
/// <param name="Outcome">The semantic classification outcome.</param>
/// <param name="ModelId">The actual model identifier reported by the provider.</param>
/// <param name="ResponseId">The provider response identifier.</param>
public sealed record IntentClassificationResult(
    IntentClassification Classification,
    IntentClassificationOutcome Outcome,
    string? ModelId,
    string? ResponseId);

/// <summary>Classifies completed Assist | HR answers against the dashboard taxonomy.</summary>
public interface IIntentClassifier
{
    /// <summary>Classifies a question and its completed answer.</summary>
    /// <param name="question">The user question.</param>
    /// <param name="answer">The validated agent answer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The classification envelope; caller cancellation propagates.</returns>
    Task<IntentClassificationResult> ClassifyAsync(string question, string answer, CancellationToken cancellationToken);
}

/// <summary>Holder for the dedicated model client used only for telemetry classification.</summary>
/// <param name="Client">The decorated provider-facing classifier client.</param>
public sealed record IntentClassificationClient(IChatClient Client);

/// <summary>Uses a dedicated Foundry model call and validates its JSON result.</summary>
/// <param name="client">The dedicated classification client.</param>
/// <param name="options">The telemetry taxonomy configuration.</param>
/// <param name="metrics">The bounded chat and AI metrics sink.</param>
/// <param name="logger">The logger used for classification failures.</param>
public sealed partial class FoundryIntentClassifier(
    IntentClassificationClient client,
    IOptions<TelemetryOptions> options,
    IChatAiMetrics metrics,
    ILogger<FoundryIntentClassifier> logger) : IIntentClassifier
{
    private const int MaximumIntentLength = 64;
    private const int MaximumExemplarLength = 160;

    private static readonly HashSet<string> Topics =
    [
        "Leave of absence", "Payroll", "Compensation & benefits", "Employee & Job data",
        "Onboarding", "Performance Reviews", "Learning & Development", "Compliance",
        "Employee relations", "Performance management", "Mobility", "End of Employment", "Other"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<IntentClassificationResult> ClassifyAsync(
        string question,
        string answer,
        CancellationToken cancellationToken)
    {
        var taxonomyVersion = options.Value.TaxonomyVersion;
        try
        {
            var prompt = $$"""
                Classify this HR interaction. Return JSON only with keys normalizedIntent,
                intentExemplar, topic, answerConfidence, and outOfScopeRejected. normalizedIntent
                must be a short stable snake_case label. intentExemplar must be a short canonical
                question and must not copy names or personal details. topic must be exactly one of:
                {{string.Join("; ", Topics)}}.
                answerConfidence must be a number from 0 to 1.
                outOfScopeRejected must be a boolean. Judge the answer's stated reason for declining,
                not merely the question's topic. The assistant answers HR and workplace policy and
                process questions. It excludes personal or employee-specific data lookups (such as
                leave balances, pay or ratings), approving or deciding requests, legal or medical
                advice, live disciplinary, grievance or employee-relations cases, and non-HR topics.
                Return true when the answer rejects all or part of the request because it falls into
                one of those exclusions.
                Return false for inability to perform an action (such as approving or extending leave)
                while offering supported help, insufficient approved evidence, or an ordinary HR Case
                referral without a scope rejection. An HR Case link alone does not imply rejection.
                Examples:
                - "That's outside what I can help with. I can answer questions about HR policies and processes, but I can't access personal records." => true
                - "That's outside what I can help with. I'm here for HR policy and process questions." => true
                - "I cannot look up individual leave balances; that is outside my scope." => true
                - "Here is the leave policy. Your specific grievance case is outside my scope; raise an HR Case." => true
                - "I cannot approve your leave extension, but I can draft an email requesting it." => false
                - "I could not verify approved guidance. Please raise an HR Case." => false
                - "Here is the leave policy. For further help, raise an HR Case." => false

                Question: {{question}}
                Answer: {{answer}}
                """;
            var response = await client.Client.GetResponseAsync(prompt, cancellationToken: cancellationToken);
            ClassificationResponse? result;
            try
            {
                result = JsonSerializer.Deserialize<ClassificationResponse>(ExtractJson(response.Text), JsonOptions);
            }
            catch (JsonException)
            {
                // The provider completed successfully; malformed output is a validation failure,
                // not a provider failure. Preserve its metadata and the normal validation path.
                result = null;
            }
            var classification = Validate(result, question, answer, taxonomyVersion);
            var outcome = classification is null
                ? IntentClassificationOutcome.InvalidResponse
                : IntentClassificationOutcome.Succeeded;
            metrics.RecordClassification(classification is null ? "invalid_response" : "succeeded");
            return new IntentClassificationResult(
                classification ?? IntentClassification.Unknown(taxonomyVersion),
                outcome,
                response.ModelId,
                response.ResponseId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Intent classification failed");
            metrics.RecordClassification("failed");
            return new IntentClassificationResult(
                IntentClassification.Unknown(taxonomyVersion), IntentClassificationOutcome.Failed, null, null);
        }
    }

    /// <summary>
    /// Validates the model output. Cosmetic label variation is normalised rather than rejected, while
    /// uncontrolled topics, out-of-range confidence, oversized values and exemplars that copy the
    /// question or answer fail closed so that no free text reaches business telemetry.
    /// </summary>
    private static IntentClassification? Validate(
        ClassificationResponse? result,
        string question,
        string answer,
        string taxonomyVersion)
    {
        if (result is null || !Topics.Contains(result.Topic) || result.AnswerConfidence is < 0 or > 1 ||
            result.OutOfScopeRejected is null)
        {
            return null;
        }

        var intent = NormalizeIntent(result.NormalizedIntent);
        var exemplar = result.IntentExemplar?.Trim() ?? string.Empty;
        return intent.Length == 0 || intent.Length > MaximumIntentLength ||
            exemplar.Length == 0 || exemplar.Length > MaximumExemplarLength ||
            CopiesInput(exemplar, question) || CopiesInput(exemplar, answer)
            ? null
            : new IntentClassification(
                intent, exemplar, result.Topic, taxonomyVersion, result.AnswerConfidence, result.OutOfScopeRejected);
    }

    private static string NormalizeIntent(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : IntentSeparatorPattern().Replace(value.Trim().ToLowerInvariant(), "_").Trim('_');

    private static bool CopiesInput(string exemplar, string input) =>
        string.Equals(exemplar, input.Trim(), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex IntentSeparatorPattern();

    private static string ExtractJson(string value)
    {
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        return start >= 0 && end >= start ? value[start..(end + 1)] : value;
    }

    private sealed record ClassificationResponse(
        string NormalizedIntent,
        string IntentExemplar,
        string Topic,
        double AnswerConfidence,
        bool? OutOfScopeRejected);
}
