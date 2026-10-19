using System.Text.Json;
using Orb.Automate.Agents.HR.Api.Contracts;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Bounds, serialises, and deserialises the conversation state kept for Teams personal chats.
/// Covers both the exchange history and any structured values (email drafts, SMART objectives)
/// that must survive across turns.
/// </summary>
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

    /// <summary>
    /// Serialises a structured state value (e.g. <see cref="EmailDraft"/>,
    /// <see cref="SmartObjective"/>) to a JSON string for storage in conversation state.
    /// </summary>
    public static string SerializeValue<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>
    /// Deserialises a structured state value previously written by <see cref="SerializeValue{T}"/>.
    /// Returns <see langword="null"/> when <paramref name="json"/> is absent or cannot be parsed
    /// (e.g. after a schema change between deployments).
    /// </summary>
    public static T? DeserializeValue<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
