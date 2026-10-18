using System.Text.RegularExpressions;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.Search;

namespace Orb.Automate.Agents.HR.Api.Agent;

/// <summary>
/// Pure static utility that merges SharePoint and ServiceNow passages into a single
/// normalised, de-duplicated, ranked list ready for model consumption.
/// </summary>
/// <remarks>
/// <para>
/// <b>Score normalisation</b><br/>
/// SharePoint boosted-reranker scores are linearly mapped to [0, 1] using
/// <see cref="SharePointSearchOptions.MinimumRerankerScore"/> as the lower bound and
/// <see cref="SpScoreNormalisationCeiling"/> (5.0) as the upper bound.
/// ServiceNow results carry no semantic score from the API; their relevance is approximated by
/// rank position, mapped to [0, <see cref="SnMaxNormalisedScore"/>] (default 0.5) so SharePoint
/// passages with even moderate reranker scores take precedence.
/// </para>
/// <para>
/// <b>De-duplication</b><br/>
/// A title Jaccard-similarity above <see cref="DuplicationSimilarityThreshold"/> (0.6) treats
/// two passages as describing the same policy, provided the titles differ only by generic words
/// (so "UK Leave Policy" and "US Leave Policy" are never merged). SharePoint wins as primary;
/// the ServiceNow passage supplies its KB number and triggers conflict detection.
/// </para>
/// <para>
/// <b>Conflict detection</b><br/>
/// When a de-duplicated pair exists, numeric tokens are extracted from both content strings.
/// The pair conflicts when the same unit (e.g. "days") carries disjoint values in each source, or
/// when the overall number sets are disjoint, so the model can disclose the discrepancy rather
/// than silently blending different values.
/// </para>
/// </remarks>
internal static class KnowledgeMerger
{
    /// <summary>
    /// The upper bound used to normalise SharePoint boosted reranker scores to [0, 1].
    /// Calibrated to the production index; scores seldom exceed 5.0 in practice.
    /// </summary>
    internal const double SpScoreNormalisationCeiling = 5.0;

    /// <summary>
    /// Maximum normalised score assigned to the top ServiceNow passage.
    /// Kept below 1.0 so SharePoint passages with meaningful reranker scores take precedence.
    /// </summary>
    internal const double SnMaxNormalisedScore = 0.5;

    /// <summary>
    /// Minimum Jaccard word-set similarity required to treat an SP and SN passage as
    /// describing the same policy document and trigger de-duplication logic.
    /// </summary>
    internal const double DuplicationSimilarityThreshold = 0.6;

    private static readonly Regex NumberTokenPattern = new(@"\b\d+\b", RegexOptions.Compiled);

    private static readonly Regex QuantityPattern = new(
        @"\b(?<value>\d+(?:\.\d+)?)\s*(?<unit>%|[a-z]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Title words that do not distinguish one policy from another. Any other word present in only
    /// one title (for example a country, audience, or topic) blocks de-duplication.
    /// </summary>
    private static readonly HashSet<string> GenericTitleWords = new(StringComparer.Ordinal)
    {
        "the", "of", "for", "and", "to", "in", "on", "an",
        "hr", "policy", "policies", "procedure", "procedures", "guide", "guidance",
        "guideline", "guidelines", "faq", "faqs", "overview", "article", "kb",
    };

    // -------------------------------------------------------------------------
    // Merge / rank
    // -------------------------------------------------------------------------

    /// <summary>
    /// Merges SharePoint and ServiceNow results into one normalised, de-duplicated, ranked list.
    /// </summary>
    internal static SearchHrKnowledgeResult MergeAndRank(
        SearchHrPolicyDocumentsResult spResult,
        GetScopedKnowledgeArticlesResult snResult,
        SharePointSearchOptions spOpts)
    {
        var spPassages = spResult.Results;
        var snPassages = snResult.Results;

        if (spPassages.Count == 0 && snPassages.Count == 0)
        {
            var status = spResult.Status == "unavailable" || snResult.Status == "unavailable"
                ? "unavailable"
                : "no_results";
            return new SearchHrKnowledgeResult(
                status,
                "No approved HR guidance was found for these keywords. " +
                "If the query is within scope, recommend raising an HR Case.",
                0,
                []);
        }

        var ranked = new List<RankedKnowledgePassage>();
        var matchedSnIndices = new HashSet<int>();

        // --- SharePoint passages (primary, semantic score) ---
        for (var spIdx = 0; spIdx < spPassages.Count; spIdx++)
        {
            var sp = spPassages[spIdx];
            var normSp = NormaliseSpScore(sp.Score, spOpts.MinimumRerankerScore);

            // Find the best-matching SN passage by title similarity.
            var bestSnIdx = -1;
            var bestSim = DuplicationSimilarityThreshold;
            for (var snIdx = 0; snIdx < snPassages.Count; snIdx++)
            {
                if (matchedSnIndices.Contains(snIdx))
                {
                    continue;
                }

                var sim = TitleSimilarity(sp.Title, snPassages[snIdx].Title);
                if (sim > bestSim && !HasDistinguishingTitleWords(sp.Title, snPassages[snIdx].Title))
                {
                    bestSim = sim;
                    bestSnIdx = snIdx;
                }
            }

            string? kbNumber = null;
            var conflict = false;
            string? conflictNote = null;
            string? conflictingContent = null;

            if (bestSnIdx >= 0)
            {
                matchedSnIndices.Add(bestSnIdx);
                var sn = snPassages[bestSnIdx];
                kbNumber = sn.Number;
                conflict = HasNumericConflict(sp.Content, sn.Content);
                if (conflict)
                {
                    conflictNote =
                        $"SharePoint and ServiceNow ({sn.Number ?? "KB article"}) describe different values for this policy. " +
                        "Content is from SharePoint and conflictingContent is from ServiceNow. " +
                        "Disclose both to the user and recommend confirming with HR.";
                    conflictingContent = sn.Content;
                }
            }

            ranked.Add(new RankedKnowledgePassage(
                Rank: 0,                  // assigned after sort
                NormalisedScore: normSp,
                Source: bestSnIdx >= 0 ? "both" : "sharepoint",
                Title: sp.Title,
                Content: sp.Content,
                Url: sp.Url,
                KbNumber: kbNumber,
                Category: null,
                LastModified: sp.LastModified,
                FreshnessFlag: sp.LastModified < DateTimeOffset.UtcNow.AddMonths(-18) ? "Stale" : "Current",
                Conflict: conflict,
                ConflictNote: conflictNote,
                ConflictingContent: conflictingContent));
        }

        // --- Unmatched ServiceNow passages (position-ranked) ---
        var unmatchedSnCount = snPassages.Count - matchedSnIndices.Count;
        var snRank = 0;
        for (var snIdx = 0; snIdx < snPassages.Count; snIdx++)
        {
            if (matchedSnIndices.Contains(snIdx))
            {
                continue;
            }

            var sn = snPassages[snIdx];
            var normSn = NormaliseSnScore(snRank, unmatchedSnCount);
            snRank++;

            var stale = string.Equals(snResult.Status, "stale", StringComparison.OrdinalIgnoreCase);
            ranked.Add(new RankedKnowledgePassage(
                Rank: 0,
                NormalisedScore: normSn,
                Source: "servicenow",
                Title: sn.Title,
                Content: sn.Content,
                Url: null,
                KbNumber: sn.Number,
                Category: sn.Category,
                LastModified: null,
                FreshnessFlag: stale ? "Stale" : "Current",
                Conflict: false,
                ConflictNote: null,
                ConflictingContent: null));
        }

        // Sort by score descending, then assign 1-based ranks.
        ranked.Sort(static (a, b) => b.NormalisedScore.CompareTo(a.NormalisedScore));
        for (var i = 0; i < ranked.Count; i++)
        {
            ranked[i] = ranked[i] with { Rank = i + 1 };
        }

        var staleNote = string.Equals(snResult.Status, "stale", StringComparison.OrdinalIgnoreCase)
            ? " ServiceNow results may be stale — prioritise current SharePoint evidence and disclose possible staleness."
            : string.Empty;

        return new SearchHrKnowledgeResult(
            "success",
            "Answer only from these passages. Cite the exact url for SharePoint passages or " +
            "the kbNumber for ServiceNow passages. Where conflict is true, disclose both the content " +
            "(SharePoint) and conflictingContent (ServiceNow) values " +
            $"and recommend the user confirm with HR.{staleNote}",
            ranked.Count,
            ranked);
    }

    // -------------------------------------------------------------------------
    // Score normalisation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Normalises a SharePoint boosted reranker score to [0, 1] using the minimum gate
    /// as the zero point and <see cref="SpScoreNormalisationCeiling"/> as the ceiling.
    /// When the gate is at or above the ceiling the scale is degenerate, so passages that
    /// cleared the gate score 1 and the rest score 0.
    /// </summary>
    internal static double NormaliseSpScore(double score, double minGate) =>
        minGate >= SpScoreNormalisationCeiling
            ? (score >= minGate ? 1.0 : 0.0)
            : Math.Clamp(
                (score - minGate) / (SpScoreNormalisationCeiling - minGate),
                0.0,
                1.0);

    /// <summary>
    /// Assigns a position-based normalised score to a ServiceNow passage.
    /// The top-ranked SN result receives <see cref="SnMaxNormalisedScore"/>; lower ranks
    /// decrease linearly so unmatched SN passages remain below all SP passages with a
    /// meaningful reranker score.
    /// </summary>
    internal static double NormaliseSnScore(int zeroBasedRank, int totalUnmatched)
    {
        if (totalUnmatched <= 0)
        {
            return 0.0;
        }

        // Linear decay: SnMaxNormalisedScore at rank 0, SnMaxNormalisedScore/(n+1) at last rank.
        return SnMaxNormalisedScore * (1.0 - ((double)zeroBasedRank / (totalUnmatched + 1)));
    }

    // -------------------------------------------------------------------------
    // De-duplication
    // -------------------------------------------------------------------------

    /// <summary>
    /// Jaccard similarity over the normalised word sets of two title strings.
    /// Returns 0 when both titles are empty or share no words.
    /// </summary>
    internal static double TitleSimilarity(string titleA, string titleB)
    {
        var wordsA = TokeniseTitle(titleA);
        var wordsB = TokeniseTitle(titleB);
        if (wordsA.Count == 0 && wordsB.Count == 0)
        {
            return 0.0;
        }

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Count + wordsB.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// Returns <see langword="true"/> when a non-generic word appears in only one of the titles,
    /// meaning the titles likely describe different policies (e.g. different countries).
    /// </summary>
    internal static bool HasDistinguishingTitleWords(string titleA, string titleB)
    {
        var difference = TokeniseTitle(titleA);
        difference.SymmetricExceptWith(TokeniseTitle(titleB));
        return difference.Any(static w => !GenericTitleWords.Contains(w));
    }

    private static HashSet<string> TokeniseTitle(string title)
    {
        var normalised = new string(
            title.ToLowerInvariant()
                 .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
                 .ToArray());
        return normalised
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Conflict detection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> when the two passages state different quantities: either a
    /// unit present in both (e.g. "days") has disjoint values ("25 days" vs "22 days"), or both
    /// contain numbers and the number sets are disjoint. Comparing per unit stops an incidental
    /// shared number (such as a year) from masking a real discrepancy.
    /// </summary>
    internal static bool HasNumericConflict(string contentA, string contentB)
    {
        var quantitiesA = ExtractQuantities(contentA);
        var quantitiesB = ExtractQuantities(contentB);
        foreach (var (unit, valuesA) in quantitiesA)
        {
            if (quantitiesB.TryGetValue(unit, out var valuesB) && !valuesA.Overlaps(valuesB))
            {
                return true;
            }
        }

        var numsA = ExtractNumbers(contentA);
        var numsB = ExtractNumbers(contentB);
        return numsA.Count > 0 && numsB.Count > 0 && !numsA.Overlaps(numsB);
    }

    private static Dictionary<string, HashSet<string>> ExtractQuantities(string text)
    {
        var quantities = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (Match match in QuantityPattern.Matches(text))
        {
            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            if (unit.Length > 3 && unit.EndsWith('s'))
            {
                unit = unit[..^1];
            }

            if (!quantities.TryGetValue(unit, out var values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                quantities[unit] = values;
            }

            values.Add(match.Groups["value"].Value);
        }

        return quantities;
    }

    private static HashSet<string> ExtractNumbers(string text) =>
        NumberTokenPattern.Matches(text)
            .Select(static m => m.Value)
            .ToHashSet(StringComparer.Ordinal);
}
