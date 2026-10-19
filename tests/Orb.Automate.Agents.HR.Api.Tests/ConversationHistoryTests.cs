using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Contracts;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests bounding and storage of Teams conversation history.</summary>
public sealed class ConversationHistoryTests
{
    /// <summary>Only the newest exchanges up to the exchange limit are kept, in original order.</summary>
    [Fact]
    public void FitKeepsNewestExchangesInOrder()
    {
        var exchanges = Enumerable.Range(1, 7).Select(i => new ConversationExchange($"q{i}", $"a{i}"));

        var kept = ConversationHistory.Fit(exchanges, maxExchanges: 3, maxCharacters: 1_000);

        Assert.Equal(["q5", "q6", "q7"], kept.Select(exchange => exchange.UserMessage));
    }

    /// <summary>Whole older exchanges are dropped once the character limit would be exceeded.</summary>
    [Fact]
    public void FitDropsWholeOldestExchangesToMeetCharacterLimit()
    {
        ConversationExchange[] exchanges =
        [
            new("old", new string('a', 50)),
            new("mid", new string('b', 30)),
            new("new", new string('c', 30)),
        ];

        var kept = ConversationHistory.Fit(exchanges, maxExchanges: 5, maxCharacters: 70);

        Assert.Equal(["mid", "new"], kept.Select(exchange => exchange.UserMessage));
    }

    /// <summary>A newest exchange larger than the character limit yields no history.</summary>
    [Fact]
    public void FitReturnsEmptyWhenNewestExchangeExceedsLimit()
    {
        var kept = ConversationHistory.Fit([new("q", new string('a', 100))], maxExchanges: 5, maxCharacters: 50);

        Assert.Empty(kept);
    }

    /// <summary>Stored history round-trips through its string form.</summary>
    [Fact]
    public void SerializeRoundTrips()
    {
        ConversationExchange[] exchanges = [new("How much leave?", "25 days."), new("And in France?", "Check local policy.")];

        var restored = ConversationHistory.Deserialize(ConversationHistory.Serialize(exchanges));

        Assert.Equal(exchanges, restored);
    }

    /// <summary>Missing or unreadable state yields empty history rather than failing the turn.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("[{\"userMessage\":\"q\"}]")]
    public void DeserializeToleratesMissingOrInvalidState(string? json)
    {
        Assert.Empty(ConversationHistory.Deserialize(json));
    }
}
