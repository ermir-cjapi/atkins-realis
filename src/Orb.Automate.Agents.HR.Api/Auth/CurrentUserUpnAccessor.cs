using System.Security.Claims;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.Auth;

/// <summary>
/// Resolves the verified current user's UPN without exposing identity to the model.
/// </summary>
public interface ICurrentUserUpnAccessor
{
    /// <summary>
    /// Gets the current user's verified UPN, or <see langword="null"/> when unavailable.
    /// </summary>
    string? GetCurrentUserUpn();
}

/// <summary>
/// Resolves identity from authenticated claims, with a Development-only local override.
/// </summary>
public sealed class CurrentUserUpnAccessor(
    IHttpContextAccessor httpContextAccessor,
    IHostEnvironment environment,
    IOptions<ServiceNowOptions> options) : ICurrentUserUpnAccessor
{
    private static readonly string[] ClaimTypesInPriorityOrder =
    [
        "preferred_username",
        "upn",
        ClaimTypes.Upn,
        ClaimTypes.Email,
        "email"
    ];

    /// <inheritdoc />
    public string? GetCurrentUserUpn()
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated is true)
        {
            foreach (var claimType in ClaimTypesInPriorityOrder)
            {
                var value = principal.FindFirst(claimType)?.Value?.Trim();
                if (IsValidUpn(value))
                {
                    return value;
                }
            }
        }

        var developmentUpn = options.Value.DevelopmentUserUpn?.Trim();
        return environment.IsDevelopment() && IsValidUpn(developmentUpn)
            ? developmentUpn
            : null;
    }

    private static bool IsValidUpn(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 320 &&
        value.Contains('@', StringComparison.Ordinal);
}
