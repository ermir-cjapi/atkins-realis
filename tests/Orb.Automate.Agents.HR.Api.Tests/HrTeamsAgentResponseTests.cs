using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Contracts;
using HrChatResponse = Orb.Automate.Agents.HR.Api.Contracts.ChatResponse;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests the Markdown reply rendered for Teams.</summary>
public sealed class HrTeamsAgentResponseTests
{
    /// <summary>
    /// Verifies that each SMART objective field is its own paragraph, because Teams collapses
    /// single newlines and would otherwise run the fields together.
    /// </summary>
    [Fact]
    public void CreateTeamsResponse_RendersSmartObjectiveFieldsAsSeparateParagraphs()
    {
        var response = new HrChatResponse(
            "Here is a SMART objective you can use.",
            null,
            null,
            SmartObjective: new SmartObjective(
                "Reduce onboarding time to 5 days by Q2.",
                Specific: "Targets onboarding duration.",
                Measurable: "Onboarding days.",
                Achievable: "Within team capacity.",
                Relevant: "Supports hiring goals.",
                TimeBound: "End of Q2."));

        var reply = HrTeamsAgent.CreateTeamsResponse(response).ReplaceLineEndings("\n");

        Assert.Equal(
            """
            Here is a SMART objective you can use.

            🎯 **SMART Objective:** Reduce onboarding time to 5 days by Q2.

            📌 **Specific:** Targets onboarding duration.

            📊 **Measurable:** Onboarding days.

            ✅ **Achievable:** Within team capacity.

            💼 **Relevant:** Supports hiring goals.

            📅 **Time-Bound:** End of Q2.
            """.ReplaceLineEndings("\n"),
            reply);
    }

    /// <summary>Verifies that empty SMART fields are omitted without leaving extra blank lines.</summary>
    [Fact]
    public void CreateTeamsResponse_OmitsEmptySmartObjectiveFields()
    {
        var response = new HrChatResponse(
            "Here you go.",
            null,
            null,
            SmartObjective: new SmartObjective("Ship the guide by June.", Measurable: " ", TimeBound: "June."));

        var reply = HrTeamsAgent.CreateTeamsResponse(response).ReplaceLineEndings("\n");

        Assert.Equal(
            """
            Here you go.

            🎯 **SMART Objective:** Ship the guide by June.

            📅 **Time-Bound:** June.
            """.ReplaceLineEndings("\n"),
            reply);
        Assert.DoesNotContain("Measurable", reply, StringComparison.Ordinal);
    }
}
