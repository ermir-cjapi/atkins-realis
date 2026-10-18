using Orb.Automate.Agents.HR.Api.Agent;
using Orb.Automate.Agents.HR.Api.Auth;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests the employee country agent tool.
/// </summary>
public sealed class GetEmployeeCountryToolTests
{
    /// <summary>
    /// Verifies that a valid Graph usage location is returned with its country name.
    /// </summary>
    [Fact]
    public async Task GetEmployeeCountryAsync_ReturnsCountryFromProfile()
    {
        var tool = new GetEmployeeCountryTool(new StubProfileProvider("gb"));

        var result = await tool.GetEmployeeCountryAsync(CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Equal("GB", result.CountryCode);
        Assert.Equal("United Kingdom", result.Country);
    }

    /// <summary>
    /// Verifies that missing or unrecognised usage locations fail closed.
    /// </summary>
    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("G1")]
    [InlineData("ZZ")]
    public async Task GetEmployeeCountryAsync_ReturnsUnavailableForUnusableValues(string usageLocation)
    {
        var tool = new GetEmployeeCountryTool(new StubProfileProvider(usageLocation));

        var result = await tool.GetEmployeeCountryAsync(CancellationToken.None);

        Assert.Equal("unavailable", result.Status);
        Assert.Null(result.CountryCode);
        Assert.Null(result.Country);
    }

    private sealed class StubProfileProvider(string country) : ICurrentUserProfileProvider
    {
        public Task<CurrentUserProfile> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CurrentUserProfile.Unknown("user@example.com") with { Country = country });
    }
}
