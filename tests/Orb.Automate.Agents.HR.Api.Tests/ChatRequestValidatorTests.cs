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

    /// <summary>
    /// Verifies the first indexed turn must be marked as the conversation start.
    /// </summary>
    [Fact]
    public void ValidateRejectsFirstMessageNotMarkedAsConversationStart()
    {
        var request = CreateRequest("Hello") with { MessageIndex = 1, IsConversationStart = false };
        var errors = ChatRequestValidator.Validate(request, 100);
        Assert.Contains(nameof(ChatRequest.MessageIndex), errors.Keys);
    }

    /// <summary>
    /// Verifies closure metadata describes the same closing turn and uses a controlled reason.
    /// </summary>
    [Fact]
    public void ValidateRejectsInvalidClosureMetadata()
    {
        var request = CreateRequest("Hello") with
        {
            Close = new ConversationClose(-1, 2, "Unknown")
        };

        var errors = ChatRequestValidator.Validate(request, 100);

        Assert.Contains("Close.ConversationDurationSeconds", errors.Keys);
        Assert.Contains("Close.TurnCount", errors.Keys);
        Assert.Contains("Close.CloseReason", errors.Keys);
    }

    /// <summary>
    /// Verifies valid closure metadata passes validation on the closing turn.
    /// </summary>
    [Fact]
    public void ValidateAcceptsValidClosureMetadata()
    {
        var request = CreateRequest("Hello") with
        {
            Close = new ConversationClose(90, 1, "UserEnded", true)
        };

        var errors = ChatRequestValidator.Validate(request, 100);

        Assert.Empty(errors);
    }

    /// <summary>
    /// Verifies that a request cannot supply both PreviousDraft and PreviousObjective, since a
    /// single turn can only be regenerating one structured artifact.
    /// </summary>
    [Fact]
    public void ValidateRejectsRequestWithBothPreviousDraftAndPreviousObjective()
    {
        var request = CreateRequest("Regenerate please.") with
        {
            PreviousDraft = new EmailDraft("Subject", "Body", null, "professional"),
            PreviousObjective = new SmartObjective("Objective text"),
        };

        var errors = ChatRequestValidator.Validate(request, 100);

        Assert.Contains(nameof(ChatRequest.PreviousObjective), errors.Keys);
    }

    /// <summary>Verifies a request with only PreviousObjective set passes validation.</summary>
    [Fact]
    public void ValidateAllowsRequestWithOnlyPreviousObjective()
    {
        var request = CreateRequest("Regenerate please.") with
        {
            PreviousObjective = new SmartObjective("Objective text"),
        };

        var errors = ChatRequestValidator.Validate(request, 100);

        Assert.Empty(errors);
    }

    private static ChatRequest CreateRequest(string? message) =>
        new(message, "conversation-1", "activity-1", 1, true);
}
