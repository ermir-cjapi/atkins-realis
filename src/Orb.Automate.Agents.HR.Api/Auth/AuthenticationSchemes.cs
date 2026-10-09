namespace Orb.Automate.Agents.HR.Api.Auth;

/// <summary>
/// Authentication schemes and principal markers used to keep delegated API identity separate from
/// Bot Connector service identity.
/// </summary>
internal static class AuthenticationSchemes
{
    internal const string HrApi = "HrApi";
    internal const string BotConnector = "BotConnector";
    internal const string PrincipalSourceClaim = "orb_auth_scheme";
}