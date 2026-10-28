using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartMatch.Matching;

/// <summary>
/// Deterministic similarity between two comparison keys, in [0, 1].
/// </summary>
public static class TitleSimilarity
{
    private static readonly string[] Articles = { "the ", "a ", "an " };

    /// <summary>
    /// Computes similarity between two comparison keys.
    /// </summary>
    /// <param name="a">First key.</param>
    /// <param name="b">Second key.</param>
    /// <returns>1 for identical keys, 0.95 when only a leading article differs, otherwise the larger of
    /// normalized edit similarity and token Dice coefficient.</returns>
    public static double Compute(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        if (string.Equals(StripArticle(a), StripArticle(b), StringComparison.Ordinal))
        {
            return 0.95;
        }

        var edit = 1.0 - ((double)Levenshtein(a, b) / Math.Max(a.Length, b.Length));
        return Math.Max(edit, Dice(a, b));
    }

    private static string StripArticle(string key)
    {
        foreach (var article in Articles)
        {
            if (key.StartsWith(article, StringComparison.Ordinal) && key.Length > article.Length)
            {
                return key[article.Length..];
            }
        }

        return key;
    }

    private static double Dice(string a, string b)
    {
        var ta = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        var tb = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        if (ta.Count == 0 || tb.Count == 0)
        {
            return 0;
        }

        var shared = ta.Count(tb.Contains);
        return 2.0 * shared / (ta.Count + tb.Count);
    }

    private static int Levenshtein(string a, string b)
    {
        // Two-row dynamic programme; keys are short (bounded by the 512-char input cap).
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
