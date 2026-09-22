using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Diagnostics;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests bounded grounding capture, isolation and privacy.</summary>
public sealed class GroundingTelemetryContextTests
{
    private const string SensitiveTerm = "alice.smith maternity leave";

    /// <summary>Verifies bounded tool facts and citation derivation are captured per turn.</summary>
    [Fact]
    public void CapturesBoundedToolAndCitationFacts()
    {
        var metrics = new RecordingChatAiMetrics();
        var context = new GroundingTelemetryContext(metrics);
        using var turn = context.BeginTurn();
        using (context.BeginInvocationMode("orchestrated"))
        {
            context.RecordSharePoint(
                new SearchHrPolicyDocumentsResult("success", "message", SensitiveTerm, 4, 2, []),
                TimeSpan.FromMilliseconds(5));
        }

        context.RecordServiceNow(
            new GetScopedKnowledgeArticlesResult("stale", "message", SensitiveTerm, 3, 1,
                [new("sys-id", "kb_knowledge", "Leave", "body", "KB1", "Policy")]),
            TimeSpan.FromMilliseconds(7));
        context.RecordCitationValidation(CitationValidationOutcomes.Passed, 3, 2, 1, false);

        var snapshot = context.Snapshot;
        Assert.Equal(2, snapshot.Invocations.Count);
        Assert.Equal("orchestrated", snapshot.Invocations[0].InvocationMode);
        Assert.Equal("model_controlled", snapshot.Invocations[1].InvocationMode);
        Assert.Equal(2, snapshot.Invocations[0].FilteredCount);
        Assert.Equal("stale", snapshot.Invocations[1].Freshness);
        Assert.Equal(7, snapshot.RetrievedCount);
        Assert.Equal(3, snapshot.ReturnedCount);
        Assert.Equal(2d / 3d, snapshot.CitationCoverage);
        Assert.Equal([GroundingSources.SharePoint, GroundingSources.ServiceNow], snapshot.RetrievedSourceSystems);
        Assert.Equal(CitationValidationOutcomes.Passed, snapshot.CitationValidationOutcome);
    }

    /// <summary>Verifies no query text reaches any captured bounded field.</summary>
    [Fact]
    public void CapturedFieldsNeverContainQueryText()
    {
        var context = new GroundingTelemetryContext(new RecordingChatAiMetrics());
        using var turn = context.BeginTurn();

        context.RecordSharePoint(
            new SearchHrPolicyDocumentsResult("success", "message", SensitiveTerm, 1, 1, []),
            TimeSpan.Zero);

        var invocation = Assert.Single(context.Snapshot.Invocations);
        string[] captured = [invocation.Tool, invocation.InvocationMode, invocation.Status, invocation.FailureKind,
            invocation.Freshness, invocation.CacheState];
        Assert.All(captured, value =>
            Assert.DoesNotContain(SensitiveTerm, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Verifies tool and grounding metrics carry only bounded, content-free tags.</summary>
    [Fact]
    public void EmitsBoundedToolAndGroundingMetrics()
    {
        var metrics = new RecordingChatAiMetrics();
        var context = new GroundingTelemetryContext(metrics);
        using var turn = context.BeginTurn();

        context.RecordSharePoint(
            new SearchHrPolicyDocumentsResult("unavailable", "message", SensitiveTerm, 0, 0, []),
            TimeSpan.FromMilliseconds(3));
        context.RecordCitationValidation(CitationValidationOutcomes.NotRequired, 0, 0, 0, false);

        var (tool, _, outcome, failureKind, _) = Assert.Single(metrics.Tools);
        Assert.Equal(GroundingTools.SharePointSearch, tool);
        Assert.Equal("failed", outcome);
        Assert.Equal("provider_unavailable", failureKind);
        Assert.Equal(GroundingSources.SharePoint, Assert.Single(metrics.Grounding).Source);
        Assert.Equal((CitationValidationOutcomes.NotRequired, null), Assert.Single(metrics.Citations));
    }

    /// <summary>Verifies a nested turn restores the enclosing turn's captured sources.</summary>
    [Fact]
    public void NestedScopesRestoreOuterSources()
    {
        var context = new GroundingTelemetryContext(new RecordingChatAiMetrics());
        using var outer = context.BeginTurn();
        context.RecordServiceNow(CreateServiceNowResult("outer"), TimeSpan.Zero);
        using (context.BeginTurn())
        {
            context.RecordServiceNow(CreateServiceNowResult("inner"), TimeSpan.Zero);
            Assert.Equal("inner", Assert.Single(context.ServiceNowSources).FileId);
        }

        Assert.Equal("outer", Assert.Single(context.ServiceNowSources).FileId);
    }

    /// <summary>Verifies capture outside a turn is ignored rather than leaking globally.</summary>
    [Fact]
    public void RecordingOutsideATurnIsIgnored()
    {
        var context = new GroundingTelemetryContext(new RecordingChatAiMetrics());

        context.RecordServiceNow(CreateServiceNowResult("orphan"), TimeSpan.Zero);

        Assert.Empty(context.ServiceNowSources);
        Assert.Same(GroundingTelemetrySnapshot.Empty, context.Snapshot);
    }

    private static GetScopedKnowledgeArticlesResult CreateServiceNowResult(string id) =>
        new("success", "message", null, 1, 1, [new(id, "kb_knowledge", "Title", "Body", "KB1", null)]);
}
