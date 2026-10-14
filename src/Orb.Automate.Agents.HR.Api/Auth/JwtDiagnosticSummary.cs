using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace Orb.Automate.Agents.HR.Api.Auth;

/// <summary>
/// Non-sensitive, unvalidated view of a JWT used only for SSO diagnostics. Never carries the
/// token itself, the user's object ID, or the UPN.
/// </summary>
internal sealed record JwtDiagnosticSummary(
    string Audience,
    string Issuer,
    string? Version,
    string? Scopes,
    string? ClientAppId,
    string? TenantId,
    DateTime ExpiresUtc)
{
    /// <summary>Reads diagnostic claims from a token without validating it.</summary>
    public static bool TryRead(string? token, out JwtDiagnosticSummary? summary, out string? failure)
    {
        summary = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            failure = "empty";
            return false;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token))
            {
                failure = "not a readable JWT";
                return false;
            }

            var jwt = handler.ReadJwtToken(token);
            summary = new JwtDiagnosticSummary(
                string.Join(' ', jwt.Audiences),
                jwt.Issuer,
                ReadClaim(jwt, "ver"),
                ReadClaim(jwt, "scp"),
                ReadClaim(jwt, "azp") ?? ReadClaim(jwt, "appid"),
                ReadClaim(jwt, "tid"),
                jwt.ValidTo);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityTokenException)
        {
            failure = exception.GetType().Name;
            return false;
        }
    }

    private static string? ReadClaim(JwtSecurityToken jwt, string type) =>
        jwt.Claims.FirstOrDefault(claim => claim.Type == type)?.Value;
}
