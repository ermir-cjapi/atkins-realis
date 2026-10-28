using System.Text;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Single source of truth for citation computing, shared by <see cref="AgentChatService"/> and
/// <see cref="DevUiSourcesAgent"/>: decides which retrieved sources an answer cites, whether the
/// insufficient-grounding fallback applies, and how the "Sources:" footer is rendered.
/// </summary>
internal static class CitationResolver
{
    /// <summary>The safe answer used when grounded SharePoint evidence was retrieved but not cited.</summary>
    internal const string InsufficientGroundingMessage =
        "I couldn't find this in the approved HR guidance I have access to, so I don't want to guess. " +
        "Please [Raise an HR Case](https://atkins.service-now.com/myportal?id=emp_taxonomy_topic&topic_id=b3434b7e1ba64ed0a440ffb7d34bcbc6).";

    /// <summary>
    /// Resolves which of the retrieved <paramref name="sources"/> the response cites. SharePoint
    /// sources are cited by exact URL; ServiceNow sources by KB number, falling back to the title.
    /// The fallback is required when SharePoint sources were retrieved but none is cited.
    /// </summary>
    /// <param name="responseText">The model response text.</param>
    /// <param name="sources">The sources retrieved for the turn, in display order.</param>
    public static CitationResolution Resolve(string responseText, IReadOnlyList<CitationSource> sources)
    {
        var cited = sources.Where(source => IsCited(responseText, source)).ToList();
        var requiresFallback =
            sources.Any(source => source.Kind == CitationSourceKind.SharePoint) &&
            !cited.Any(source => source.Kind == CitationSourceKind.SharePoint);
        return new CitationResolution(cited, requiresFallback);
    }

    /// <summary>
    /// Builds the "Sources:" footer with one short Markdown link per distinct source, or an empty
    /// string when there are no sources. Sources without a URL show their KB number instead.
    /// </summary>
    /// <param name="sources">The cited sources, in display order.</param>
    public static string FormatSourcesFooter(IEnumerable<CitationSource> sources)
    {
        var distinct = sources
            .DistinctBy(source => NullIfBlank(source.Url) ?? NullIfBlank(source.KbNumber) ?? source.Title,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder().AppendLine().AppendLine().AppendLine("Sources:");
        foreach (var source in distinct)
        {
            var title = source.Title.Replace('\r', ' ').Replace('\n', ' ').Replace('[', '(').Replace(']', ')').Trim();
            if (NullIfBlank(source.Url) is { } url)
            {
                builder.Append("- [").Append(title).Append("](").Append(url).AppendLine(")");
            }
            else if (NullIfBlank(source.KbNumber) is { } kbNumber)
            {
                builder.Append("- ").Append(title).Append(" (").Append(kbNumber).AppendLine(")");
            }
            else
            {
                builder.Append("- ").AppendLine(title);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static bool IsCited(string responseText, CitationSource source) =>
        source.Kind == CitationSourceKind.SharePoint
            ? NullIfBlank(source.Url) is { } url && responseText.Contains(url, StringComparison.Ordinal)
            : NullIfBlank(source.KbNumber) is { } kbNumber
                ? responseText.Contains(kbNumber, StringComparison.OrdinalIgnoreCase)
                : NullIfBlank(source.Title) is { } title &&
                    responseText.Contains(title, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>The system a retrieved source comes from, which determines how it is cited.</summary>
internal enum CitationSourceKind
{
    /// <summary>A SharePoint passage, cited by exact URL.</summary>
    SharePoint,

    /// <summary>A ServiceNow article, cited by KB number or title.</summary>
    ServiceNow,
}

/// <summary>A source retrieved for a turn that the answer may cite.</summary>
/// <param name="Kind">The source system.</param>
/// <param name="Title">The source title.</param>
/// <param name="Url">The source URL, when available.</param>
/// <param name="KbNumber">The ServiceNow KB number, when available.</param>
internal sealed record CitationSource(CitationSourceKind Kind, string Title, string? Url, string? KbNumber);

/// <summary>The outcome of resolving citations for one response.</summary>
/// <param name="Cited">The retrieved sources the response cites, in retrieval order.</param>
/// <param name="RequiresFallback">
/// Whether SharePoint evidence was retrieved but not cited, so the answer must be replaced by
/// <see cref="CitationResolver.InsufficientGroundingMessage"/>.
/// </param>
internal sealed record CitationResolution(IReadOnlyList<CitationSource> Cited, bool RequiresFallback);
