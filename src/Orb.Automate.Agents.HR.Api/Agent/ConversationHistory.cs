using System.Text.Json;
using Orb.Automate.Agents.HR.Api.Contracts;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>Bounds and serialises the conversation history kept for Teams personal chats.</summary>
internal static class ConversationHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Keeps the newest exchanges that fit both limits, dropping the oldest whole exchanges first.
    /// </summary>
    public static IReadOnlyList<ConversationExchange> Fit(
        IEnumerable<ConversationExchange> exchanges,
        int maxExchanges,
        int maxCharacters)
    {
        var kept = new List<ConversationExchange>();
        var characters = 0;
        foreach (var exchange in exchanges.Reverse())
        {
            var size = exchange.UserMessage.Length + exchange.AssistantMessage.Length;
            if (kept.Count >= maxExchanges || characters + size > maxCharacters)
            {
                break;
            }

            kept.Add(exchange);
            characters += size;
        }

        kept.Reverse();
        return kept;
    }

    /// <summary>Reads history stored by <see cref="Serialize"/>; unreadable state yields no history.</summary>
    public static IReadOnlyList<ConversationExchange> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<ConversationExchange[]>(json, JsonOptions)?
                .Where(exchange => exchange is { UserMessage: not null, AssistantMessage: not null })
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serialises history to a string so turn-state storage needs no type information.</summary>
    public static string Serialize(IReadOnlyList<ConversationExchange> exchanges) =>
        JsonSerializer.Serialize(exchanges, JsonOptions);
}
