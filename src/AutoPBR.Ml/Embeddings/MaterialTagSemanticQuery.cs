namespace AutoPBR.Core.Embeddings;

/// <summary>
/// Builds the text embedded for MiniLM material-tag matching. Only the file title is used for standard
/// asset paths so that folder names like <c>block</c>, <c>item</c>, or mod namespaces do not bias the
/// model. OptiFine paths keep full segments so CTM/plant layouts retain disambiguating context.
/// Underscores are treated as spaces; standalone directional words, <c>flipped</c>, purely numeric tokens, and common
/// English bridging/stop words (e.g. <c>and</c>, <c>the</c>, prepositions) are removed so they do not dilute the embedding query.
/// (OptiFine CTM tiles often use numeric PNG names; material context comes from folders.)
/// </summary>
public static class MaterialTagSemanticQuery
{
    private static readonly HashSet<string> ExcludedWholeWordTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        // UV / tile / orientation (existing)
        "bottom", "top", "side", "sides", "front", "forward", "back", "left", "right",
        "flipped",
        // Articles & coordinating conjunctions
        "a", "an", "the", "and", "or", "but", "nor", "yet", "so", "both", "either", "neither",
        // Pronouns & determiners (rare in titles but common if path segments leak in)
        "i", "me", "my", "we", "us", "our", "you", "your", "he", "him", "his", "she", "her", "hers",
        "it", "its", "they", "them", "their", "what", "which", "who", "whom", "whose", "this", "that",
        "these", "those", "some", "any", "each", "every", "all", "few", "such", "same", "other",
        // Verbs / auxiliaries
        "am", "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "having",
        "do", "does", "did", "done", "will", "would", "shall", "should", "can", "could", "may", "might", "must",
        // Negation / adverbs
        "not", "no", "only", "just", "also", "too", "very", "than", "then",
        // Prepositions & bridging particles
        "at", "by", "for", "from", "in", "into", "of", "off", "on", "onto", "out", "over", "to", "up",
        "upon", "with", "without", "within", "about", "above", "across", "after", "against", "along", "among",
        "around", "before", "behind", "below", "beneath", "beside", "besides", "between", "beyond", "despite",
        "during", "except", "inside", "outside", "through", "throughout", "till", "until", "via", "per", "plus",
        "as", "if", "how", "when", "where", "why", "again", "once", "here", "there", "now", "more", "most",
        "like", "near", "next", "past", "since", "toward", "towards", "under", "underneath", "unlike", "versus", "vs"
    };

    /// <summary>
    /// Non-OptiFine: file title only (processed). OptiFine: title plus path segments. See class summary for token filtering.
    /// </summary>
    public static string Build(string textureName, string ruleRelativeKey)
    {
        var name = textureName.Trim();
        if (string.IsNullOrEmpty(ruleRelativeKey))
        {
            return FilterQueryTokens(name);
        }

        var segments = ruleRelativeKey.Split((char[])['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return FilterQueryTokens(name);
        }

        if (ContainsOptifineSegment(segments))
        {
            var combined = $"{name} {string.Join(" ", segments)}".Trim();
            return FilterQueryTokens(combined);
        }

        return FilterQueryTokens(name);
    }

    /// <summary>
    /// Returns normalized semantic terms from raw title/query text.
    /// </summary>
    public static IReadOnlyList<string> ExtractTerms(string raw)
    {
        var filtered = FilterQueryTokens(raw);
        if (string.IsNullOrWhiteSpace(filtered))
        {
            return [];
        }

        return filtered
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Same token set as <see cref="ExtractTerms"/> (distinct, first occurrence order, min length 3, max 8) but order is explicit
    /// for dictionary term weighting (e.g. material word before <c>door</c> / <c>trapdoor</c>).
    /// </summary>
    public static IReadOnlyList<string> ExtractOrderedDictionaryTerms(string raw)
    {
        var filtered = FilterQueryTokens(raw);
        if (string.IsNullOrWhiteSpace(filtered))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var t in filtered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (t.Length < 3)
            {
                continue;
            }

            if (!seen.Add(t))
            {
                continue;
            }

            list.Add(t);
            if (list.Count >= 8)
            {
                break;
            }
        }

        return list;
    }

    /// <summary>
    /// Replaces underscores with spaces, drops excluded whole-word tokens (directionals, <c>flipped</c>, common English
    /// stop words) and all-digit tokens.
    /// </summary>
    private static string FilterQueryTokens(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var normalized = raw.Replace('_', ' ');
        var tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return "";
        }

        var kept = new List<string>(tokens.Length);
        foreach (var t in tokens)
        {
            if (t.Length == 0)
            {
                continue;
            }

            if (ExcludedWholeWordTerms.Contains(t))
            {
                continue;
            }

            if (IsStandaloneNumericToken(t))
            {
                continue;
            }

            kept.Add(t);
        }

        return kept.Count == 0 ? "" : string.Join(" ", kept);
    }

    private static bool IsStandaloneNumericToken(ReadOnlySpan<char> token)
    {
        if (token.IsEmpty)
        {
            return false;
        }

        foreach (var ch in token)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsOptifineSegment(ReadOnlySpan<string> segments)
    {
        foreach (var segment in segments)
        {
            if (segment.Equals("optifine", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
