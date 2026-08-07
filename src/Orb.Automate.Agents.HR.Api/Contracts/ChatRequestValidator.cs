namespace Orb.Automate.Agents.HR.Api.Contracts;

/// <summary>
/// Validates incoming chat requests.
/// </summary>
public static class ChatRequestValidator
{
    /// <summary>
    /// Validates the request payload against required input rules.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="maxInputCharacters">The maximum allowed message length.</param>
    /// <returns>A validation error dictionary keyed by field name.</returns>
    public static Dictionary<string, string[]> Validate(ChatRequest request, int maxInputCharacters)
        => string.IsNullOrWhiteSpace(request.Message)
            ? new Dictionary<string, string[]> { [nameof(request.Message)] = ["Message is required."] }
            : request.Message.Length > maxInputCharacters
                ? new Dictionary<string, string[]>
                {
                    [nameof(request.Message)] = [$"Message cannot exceed {maxInputCharacters} characters."]
                }
                : [];
}
