using Orb.Automate.Agents.HR.Api.Contracts;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests validation rules for chat requests.
/// </summary>
public sealed class ChatRequestValidatorTests
{
    /// <summary>
    /// Verifies empty input values produce a validation error.
    /// </summary>
    /// <param name="message">The input message under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateReturnsErrorForEmptyMessage(string? message)
    {
        var errors = ChatRequestValidator.Validate(CreateRequest(message), 100);
        Assert.Contains(nameof(ChatRequest.Message), errors.Keys);
    }

    /// <summary>
    /// Verifies messages that exceed the maximum length produce a validation error.
    /// </summary>
    [Fact]
    public void ValidateReturnsErrorForMessageOverLimit()
    {
        var errors = ChatRequestValidator.Validate(CreateRequest("12345"), 4);
        Assert.Contains(nameof(ChatRequest.Message), errors.Keys);
    }

    /// <summary>
    /// Verifies valid messages pass validation.
    /// </summary>
    [Fact]
    public void ValidateReturnsNoErrorsForValidMessage()
    {
        var errors = ChatRequestValidator.Validate(CreateRequest("Hello"), 100);
        Assert.Empty(errors);
    }

    /// <summary>Verifies required lifecycle identifiers are enforced.</summary>
    [Fact]
    public void ValidateReturnsErrorsForMissingLifecycleIdentifiers()
    {
        var errors = ChatRequestValidator.Validate(new ChatRequest("Hello"), 100);

        Assert.Contains(nameof(ChatRequest.ConversationId), errors.Keys);
        Assert.Contains(nameof(ChatRequest.ActivityId), errors.Keys);
        Assert.Contains(nameof(ChatRequest.MessageIndex), errors.Keys);
    }

    /// <summary>Verifies a start marker is valid only on the first user message.</summary>
    [Fact]
    public void ValidateRejectsConversationStartAfterFirstMessage()
    {
        var request = CreateRequest("Hello") with { MessageIndex = 2, IsConversationStart = true };
        var errors = ChatRequestValidator.Validate(request, 100);
        Assert.Contains(nameof(ChatRequest.MessageIndex), errors.Keys);
    }

    /// <summary>Verifies supplied close metadata must describe the current turn count.</summary>
    [Fact]
    public void ValidateRejectsCloseTurnCountBeforeCurrentMessage()
    {
        var request = CreateRequest("Bye") with
        {
            MessageIndex = 3,
            IsConversationStart = false,
            Close = new ConversationClose(60, 2, "UserEnded", true)
        };
        var errors = ChatRequestValidator.Validate(request, 100);
        Assert.Contains("Close.TurnCount", errors.Keys);
    }

    private static ChatRequest CreateRequest(string? message) =>
        new(message, "conversation-1", "activity-1", 1, true);
}
