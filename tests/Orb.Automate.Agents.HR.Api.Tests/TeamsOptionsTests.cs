using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests Teams Bot Connector configuration guardrails.</summary>
public sealed class TeamsOptionsTests
{
    /// <summary>Disabled Teams hosting does not require identity settings.</summary>
    [Fact]
    public void DisabledTeamsConfigurationIsValid() =>
        Assert.True(TeamsOptions.IsValid(new TeamsOptions()));

    /// <summary>Enabled Teams hosting rejects missing identity settings.</summary>
    [Fact]
    public void EnabledTeamsConfigurationRequiresIdentity() =>
        Assert.False(TeamsOptions.IsValid(new TeamsOptions { Enabled = true }));

    /// <summary>Enabled Teams hosting requires GUID identity values.</summary>
    [Theory]
    [InlineData("not-an-app-id", "00000000-0000-0000-0000-000000000002")]
    [InlineData("00000000-0000-0000-0000-000000000001", "not-a-tenant-id")]
    public void EnabledTeamsConfigurationRejectsInvalidIdentity(string appId, string tenantId) =>
        Assert.False(TeamsOptions.IsValid(new TeamsOptions
        {
            Enabled = true,
            AppId = appId,
            TenantId = tenantId,
        }));

    /// <summary>A complete managed-identity configuration is accepted.</summary>
    [Fact]
    public void EnabledTeamsConfigurationAcceptsManagedIdentity() =>
        Assert.True(TeamsOptions.IsValid(new TeamsOptions
        {
            Enabled = true,
            AppId = "00000000-0000-0000-0000-000000000001",
            TenantId = "00000000-0000-0000-0000-000000000002",
        }));

    /// <summary>SSO cannot run without a named Agents SDK authorization handler.</summary>
    [Fact]
    public void EnabledSsoRequiresAuthorizationHandler() =>
        Assert.False(TeamsOptions.IsValid(new TeamsOptions
        {
            Enabled = true,
            SsoEnabled = true,
            AppId = "00000000-0000-0000-0000-000000000001",
            TenantId = "00000000-0000-0000-0000-000000000002",
            AuthorizationHandler = "",
        }));
}