using System.Globalization;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>The employee's verified profile country, as supplied to the model.</summary>
/// <param name="Code">The ISO 3166-1 alpha-2 country code.</param>
/// <param name="Name">The English country name.</param>
public sealed record EmployeeCountry(string Code, string Name)
{
    /// <summary>
    /// Maps a Microsoft Graph <c>usageLocation</c> value to a country, failing closed with
    /// <see langword="null"/> on missing or unexpected values.
    /// </summary>
    public static EmployeeCountry? FromUsageLocation(string? usageLocation)
    {
        var code = usageLocation?.Trim();
        if (code is not { Length: 2 } || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]))
        {
            return null;
        }

        code = code.ToUpperInvariant();
        try
        {
            return new EmployeeCountry(code, new RegionInfo(code).EnglishName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
