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
        var errors = ChatRequestValidator.Validate(new ChatRequest(message), 100);
        Assert.Contains(nameof(ChatRequest.Message), errors.Keys);
    }

    /// <summary>
    /// Verifies messages that exceed the maximum length produce a validation error.
    /// </summary>
    [Fact]
    public void ValidateReturnsErrorForMessageOverLimit()
    {
        var errors = ChatRequestValidator.Validate(new ChatRequest("12345"), 4);
        Assert.Contains(nameof(ChatRequest.Message), errors.Keys);
    }

    /// <summary>
    /// Verifies valid messages pass validation.
    /// </summary>
    [Fact]
    public void ValidateReturnsNoErrorsForValidMessage()
    {
        var errors = ChatRequestValidator.Validate(new ChatRequest("Hello"), 100);
        Assert.Empty(errors);
    }
}
