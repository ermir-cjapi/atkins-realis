namespace Orb.Automate.Agents.HR.Api.Contracts;

/// <summary>
/// Validates incoming chat requests.
/// </summary>
public static class ChatRequestValidator
{
    private const int MaximumIdentifierLength = 512;
    private static readonly HashSet<string> CloseReasons =
        ["UserEnded", "InactivityTimeout", "Escalated"];

    /// <summary>
    /// Validates the request payload against required input rules.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="maxInputCharacters">The maximum allowed message length.</param>
    /// <returns>A validation error dictionary keyed by field name.</returns>
    public static Dictionary<string, string[]> Validate(ChatRequest request, int maxInputCharacters)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            errors[nameof(request.Message)] = ["Message is required."];
        }
        else if (request.Message.Length > maxInputCharacters)
        {
            errors[nameof(request.Message)] = [$"Message cannot exceed {maxInputCharacters} characters."];
        }

        ValidateIdentifier(request.ConversationId, nameof(request.ConversationId), errors);
        ValidateIdentifier(request.ActivityId, nameof(request.ActivityId), errors);

        if (request.MessageIndex is null or < 1)
        {
            errors[nameof(request.MessageIndex)] = ["MessageIndex must be a positive one-based index."];
        }
        else if (request.IsConversationStart != (request.MessageIndex == 1))
        {
            errors[nameof(request.MessageIndex)] =
                ["IsConversationStart must be true exactly when MessageIndex is 1."];
        }

        if (request.Close is { } close)
        {
            if (close.ConversationDurationSeconds < 0)
            {
                errors[$"{nameof(request.Close)}.{nameof(close.ConversationDurationSeconds)}"] =
                    ["ConversationDurationSeconds cannot be negative."];
            }

            if (close.TurnCount < 1 || (request.MessageIndex is { } index && close.TurnCount != index))
            {
                errors[$"{nameof(request.Close)}.{nameof(close.TurnCount)}"] =
                    ["TurnCount must equal MessageIndex on the closing turn."];
            }

            if (!CloseReasons.Contains(close.CloseReason))
            {
                errors[$"{nameof(request.Close)}.{nameof(close.CloseReason)}"] =
                    ["CloseReason must be UserEnded, InactivityTimeout, or Escalated."];
            }
        }

        return errors;
    }

    private static void ValidateIdentifier(
        string? value,
        string propertyName,
        IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[propertyName] = [$"{propertyName} is required."];
        }
        else if (value.Length > MaximumIdentifierLength)
        {
            errors[propertyName] = [$"{propertyName} cannot exceed {MaximumIdentifierLength} characters."];
        }
    }
}
