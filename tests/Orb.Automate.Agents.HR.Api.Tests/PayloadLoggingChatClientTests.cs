using Microsoft.Extensions.AI;
using Orb.Automate.Agents.HR.Api.Diagnostics;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests the development log that records the country sent to the model.</summary>
public sealed class PayloadLoggingChatClientTests
{
    /// <summary>Verifies that only the profile-country line is read from a Teams or API prompt.</summary>
    [Fact]
    public void FindPromptCountry_ReadsTheProfileLineOnly()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, """
                User question:
                annual leave

                [EMPLOYEE CONTEXT (verified by the application)]
                Profile country: India (IN)
                [/EMPLOYEE CONTEXT]
                """)
        };

        Assert.Equal("India (IN)", PayloadLoggingChatClient.FindPromptCountry(messages));
    }

    /// <summary>Verifies that an Aspire DevUI message, which has no employee context, is reported as absent.</summary>
    [Fact]
    public void FindPromptCountry_IsAbsentWithoutEmployeeContext()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "i would like to understand my annual leave")
        };

        Assert.Null(PayloadLoggingChatClient.FindPromptCountry(messages));
    }
}
