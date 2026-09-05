using System;
using System.Collections.Generic;
using System.Linq;

namespace Evania.Services;

public static class SearchService
{
    public static string[] SplitTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text.Trim().Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static IEnumerable<(int Start, int Length)> FindWordPrefixMatchRanges(string? text, string? filter)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(filter))
        {
            yield break;
        }

        string[] filterTerms = SplitTerms(filter);

        foreach (string term in filterTerms)
        {
            int textIndex = 0;
            while (textIndex < text.Length)
            {
                while (textIndex < text.Length && char.IsWhiteSpace(text[textIndex]))
                {
                    textIndex++;
                }

                int wordStart = textIndex;
                while (textIndex < text.Length && !char.IsWhiteSpace(text[textIndex]))
                {
                    textIndex++;
                }

                if (textIndex > wordStart && text.AsSpan(wordStart, textIndex - wordStart).StartsWith(term, StringComparison.CurrentCultureIgnoreCase))
                {
                    yield return (wordStart, term.Length);
                }
            }
        }
    }

    public static bool MatchesWordPrefixes(string? text, IReadOnlyList<string> filterTerms)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] words = text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return filterTerms.All(term => words.Any(word =>
            word.StartsWith(term, StringComparison.CurrentCultureIgnoreCase)));
    }
}
