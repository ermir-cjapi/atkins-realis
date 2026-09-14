namespace Orb.Automate.Agents.HR.Api.Auth;

/// <summary>
/// Supplies a token representing the current end user, scoped to the Azure AI Search data plane.
/// The token is used by the search service to evaluate document-level permissions.
/// </summary>
public interface ISearchUserTokenProvider
{
    /// <summary>
    /// Gets a search-scoped token for the current user.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The token, or <see langword="null"/> when no verified user identity is available.</returns>
    Task<string?> GetTokenAsync(CancellationToken cancellationToken);
}
