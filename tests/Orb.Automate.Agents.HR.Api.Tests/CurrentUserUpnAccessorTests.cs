using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Auth;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests trusted current-user identity resolution.
/// </summary>
public sealed class CurrentUserUpnAccessorTests
{
    /// <summary>
    /// Authenticated claims take priority over the Development override.
    /// </summary>
    [Fact]
    public void AuthenticatedPreferredUsernameTakesPriority()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", "verified@example.com")],
            "Test"));
        var context = new DefaultHttpContext { User = principal };
        var accessor = CreateAccessor(context, "Development", "local@example.com");

        Assert.Equal("verified@example.com", accessor.GetCurrentUserUpn());
    }

    /// <summary>
    /// A Bot Connector service token is never accepted as the employee identity.
    /// </summary>
    [Fact]
    public void BotConnectorPrincipalCannotSupplyUserUpn()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("preferred_username", "service@example.com"),
                new Claim(AuthenticationSchemes.PrincipalSourceClaim, AuthenticationSchemes.BotConnector),
            ],
            "Test"));
        var context = new DefaultHttpContext { User = principal };
        var accessor = CreateAccessor(context, "Production", null);

        Assert.Null(accessor.GetCurrentUserUpn());
    }

    /// <summary>
    /// A validated Teams turn identity supplies the employee UPN even though the HTTP principal is the connector.
    /// </summary>
    [Fact]
    public void ValidatedTeamsIdentityTakesPriorityOverConnectorPrincipal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("preferred_username", "service@example.com"),
                new Claim(AuthenticationSchemes.PrincipalSourceClaim, AuthenticationSchemes.BotConnector),
            ],
            "Test"));
        var context = new DefaultHttpContext { User = principal };
        var teamsContext = new TeamsUserContextAccessor();
        using var scope = teamsContext.Push(new TeamsUserContext(
            "delegated-token",
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            "employee@example.com"));
        var accessor = CreateAccessor(context, "Production", null, teamsContext);

        Assert.Equal("employee@example.com", accessor.GetCurrentUserUpn());
    }

    /// <summary>
    /// Local development can use an explicitly configured test identity.
    /// </summary>
    [Fact]
    public void DevelopmentUsesConfiguredTestUpn()
    {
        var accessor = CreateAccessor(new DefaultHttpContext(), "Development", "local@example.com");

        Assert.Equal("local@example.com", accessor.GetCurrentUserUpn());
    }

    /// <summary>
    /// Deployed environments never use the development identity override.
    /// </summary>
    [Fact]
    public void ProductionRejectsDevelopmentTestUpn()
    {
        var accessor = CreateAccessor(new DefaultHttpContext(), "Production", "local@example.com");

        Assert.Null(accessor.GetCurrentUserUpn());
    }

    private static CurrentUserUpnAccessor CreateAccessor(
        HttpContext context,
        string environmentName,
        string? developmentUpn,
        ITeamsUserContextAccessor? teamsUserContextAccessor = null)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = context };
        var settings = Microsoft.Extensions.Options.Options.Create(
            new ServiceNowOptions { DevelopmentUserUpn = developmentUpn });
        return new CurrentUserUpnAccessor(
            httpContextAccessor,
            teamsUserContextAccessor ?? new TeamsUserContextAccessor(),
            new TestHostEnvironment(environmentName),
            settings);
    }
}
