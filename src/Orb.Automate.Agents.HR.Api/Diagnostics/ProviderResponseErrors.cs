using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Orb.Automate.Agents.HR.Api.Diagnostics;

/// <summary>Converts provider error payloads into failures without exposing their sensitive text.</summary>
internal static class ProviderResponseErrors
{
    // Both OpenAI adapters report model refusals with this code. A refusal is a valid model
    // answer, not a provider failure, so it must not abort the turn.
    private const string RefusalErrorCode = "Refusal";

    /// <summary>
    /// Determines whether a response or update represents a provider failure. Failures arrive either
    /// as error content (Responses <c>error</c> events) or as a failed Responses status
    /// (<c>response.failed</c>), which the adapter surfaces without error content.
    /// </summary>
    public static bool IsFailed(IEnumerable<AIContent> contents, object? rawRepresentation) =>
        contents.Any(content => content is ErrorContent { ErrorCode: not RefusalErrorCode }) ||
        IsFailedRepresentation(rawRepresentation);

    /// <summary>Creates the sanitized failure raised for provider errors.</summary>
    public static InvalidOperationException CreateFailure() =>
        new("The AI provider returned an unsuccessful response.");

    private static bool IsFailedRepresentation(object? rawRepresentation) => rawRepresentation switch
    {
        ChatResponseUpdate update => IsFailedRepresentation(update.RawRepresentation),
        ChatResponse response => IsFailedRepresentation(response.RawRepresentation),
        StreamingResponseFailedUpdate => true,
        ResponseResult result => result.Status == ResponseStatus.Failed,
        _ => false
    };
}