using System.ComponentModel;
using System.Globalization;
using Orb.Automate.Agents.HR.Api.Auth;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Agent tool that returns the current employee's country from the Microsoft Graph
/// <c>usageLocation</c> attribute, resolved through the verified user identity.
/// </summary>
public sealed class GetEmployeeCountryTool(ICurrentUserProfileProvider profileProvider)
{
    /// <summary>Gets the current employee's country.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    [Description(
        "Returns the current employee's country from their Microsoft Entra profile. " +
        "Use when Leave of Absence guidance may vary by country. Takes no arguments.")]
    public async Task<GetEmployeeCountryResult> GetEmployeeCountryAsync(CancellationToken cancellationToken)
    {
        var profile = await profileProvider.GetCurrentAsync(cancellationToken);
        return GetEmployeeCountryResult.FromUsageLocation(profile.Country);
    }
}

/// <summary>The agent-facing employee country result.</summary>
/// <param name="Status"><c>success</c> or <c>unavailable</c>.</param>
/// <param name="CountryCode">The ISO 3166-1 alpha-2 country code, when available.</param>
/// <param name="Country">The English country name, when available.</param>
public sealed record GetEmployeeCountryResult(string Status, string? CountryCode, string? Country)
{
    /// <summary>The result returned when no country can be verified.</summary>
    public static GetEmployeeCountryResult Unavailable { get; } = new("unavailable", null, null);

    /// <summary>Maps a Graph <c>usageLocation</c> value to a result, failing closed on unexpected values.</summary>
    public static GetEmployeeCountryResult FromUsageLocation(string? usageLocation)
    {
        var code = usageLocation?.Trim();
        if (code is not { Length: 2 } || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]))
        {
            return Unavailable;
        }

        code = code.ToUpperInvariant();
        try
        {
            return new GetEmployeeCountryResult("success", code, new RegionInfo(code).EnglishName);
        }
        catch (ArgumentException)
        {
            return Unavailable;
        }
    }
}
