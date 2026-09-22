using System.ComponentModel.DataAnnotations;

namespace Orb.Automate.Agents.HR.Api.Options;

/// <summary>Configuration for Microsoft Graph user-profile enrichment.</summary>
public sealed class GraphProfileOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "GraphProfile";

    /// <summary>Gets whether profile enrichment is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the Microsoft Graph base URI.</summary>
    [Url]
    public string BaseUri { get; init; } = "https://graph.microsoft.com/v1.0/";

    /// <summary>Gets the profile-cache lifetime in minutes.</summary>
    [Range(1, 1440)]
    public int CacheMinutes { get; init; } = 60;

    /// <summary>Gets the Graph request timeout in seconds.</summary>
    [Range(1, 30)]
    public int RequestTimeoutSeconds { get; init; } = 5;
}
