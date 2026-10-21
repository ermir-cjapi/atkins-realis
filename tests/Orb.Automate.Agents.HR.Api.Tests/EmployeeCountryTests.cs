using Orb.Automate.Agents.HR.Api.Agent;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>Tests mapping a Graph <c>usageLocation</c> to the employee country.</summary>
public sealed class EmployeeCountryTests
{
    /// <summary>Verifies that a valid code is normalised and named.</summary>
    [Fact]
    public void FromUsageLocation_MapsValidCode()
    {
        var country = EmployeeCountry.FromUsageLocation(" gb ");

        Assert.NotNull(country);
        Assert.Equal("GB", country.Code);
        Assert.Equal("United Kingdom", country.Name);
    }

    /// <summary>Verifies that missing or unexpected values fail closed.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("G1")]
    [InlineData("ZZ")]
    public void FromUsageLocation_ReturnsNullForInvalidValues(string? usageLocation)
    {
        Assert.Null(EmployeeCountry.FromUsageLocation(usageLocation));
    }
}
