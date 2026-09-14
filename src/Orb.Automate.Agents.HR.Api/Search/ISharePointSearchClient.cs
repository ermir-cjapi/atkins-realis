namespace Orb.Automate.Agents.HR.Api.Search;

/// <summary>
/// Queries the SharePoint knowledge index with document-level permission trimming.
/// </summary>
public interface ISharePointSearchClient
{
    /// <summary>
    /// Runs a hybrid, semantically ranked search trimmed to the supplied user's permissions.
    /// </summary>
    /// <param name="term">The generated search keywords.</param>
    /// <param name="userToken">The end user's token, forwarded to the service for permission evaluation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The retrieved results.</returns>
    Task<SharePointSearchResponse> SearchAsync(string term, string userToken, CancellationToken cancellationToken);
}
