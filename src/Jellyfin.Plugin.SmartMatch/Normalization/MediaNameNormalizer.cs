using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SmartMatch.Normalization;

/// <summary>
/// Derives a clean search title from a noisy library name.
/// </summary>
public interface IMediaNameNormalizer
{
    /// <summary>
    /// Normalizes one name.
    /// </summary>
    /// <param name="input">Name and provenance.</param>
    /// <param name="rules">Active prefix rules.</param>
    /// <returns>The normalization result with a full trace.</returns>
    NormalizationResult Normalize(MediaNameInput input, RuleSet rules);
}

/// <summary>
/// Pure, deterministic normalizer. It never touches the filesystem, the network or Jellyfin.
/// Release tokens are only removed from a recognisable suffix: the title ends at the first
/// episode marker, trustworthy year or unambiguous release token, and ordinary words such as
/// "Web", "Complete" or "Up" are never removed from inside a title.
/// </summary>
public sealed partial class MediaNameNormalizer : IMediaNameNormalizer
{
    /// <summary>Maximum accepted input length; longer names are truncated with a warning.</summary>
    public const int MaxInputLength = 512;

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "m4v", "avi", "mov", "wmv", "ts", "m2ts", "mts", "webm", "mpg", "mpeg", "flv", "iso",
        "vob", "divx", "ogm", "ogv", "3gp", "rmvb", "strm", "img",
    };

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mr", "Mrs", "Ms", "Dr", "St", "Jr", "Sr", "Vs", "Prof", "Mt", "Ft", "Lt", "Sgt", "Capt", "Gen", "Col", "Rev",
    };

    private static readonly HashSet<string> Articles = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an" };

    private static readonly string[] CountryQualifiers = { "US", "UK", "AU", "NZ", "CA", "IE" };

    private readonly Func<int> _maxYear;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaNameNormalizer"/> class.
    /// </summary>
    public MediaNameNormalizer()
        : this(() => DateTime.UtcNow.Year + 2)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaNameNormalizer"/> class with a fixed year horizon (tests).
    /// </summary>
    /// <param name="maxYear">Largest number accepted as a production year.</param>
    public MediaNameNormalizer(Func<int> maxYear)
    {
        _maxYear = maxYear;
    }

    /// <summary>
    /// Builds the comparison key: Unicode-folded, casefolded and punctuation-insensitive.
    /// "Mr. Robot", "mr robot" and "Mr.Robot" share a key; display text is never derived from it.
    /// </summary>
    /// <param name="title">Title text.</param>
    /// <returns>The key.</returns>
    public static string ComparisonKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var decomposed = title.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (ch is '\'' or '’' or '‘' or '`' or 'ʼ')
            {
                continue;
            }

            if (ch == '&')
            {
                sb.Append(" and ");
                continue;
            }

            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return WhitespaceRegex().Replace(sb.ToString(), " ").Trim();
    }

    /// <inheritdoc />
    public NormalizationResult Normalize(MediaNameInput input, RuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);

        var steps = new List<NormalizationStep>();
        var warnings = new List<string>();
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var original = input.RawName ?? string.Empty;

        var s = original.Normalize(NormalizationForm.FormC);
        s = ControlCharRegex().Replace(s, " ").Trim();
        if (s.Length > MaxInputLength)
        {
            s = s[..MaxInputLength];
            warnings.Add("name longer than 512 characters was truncated");
        }

        // 1. Known media extension (files only).
        if (input.IsFile)
        {
            var dot = s.LastIndexOf('.');
            if (dot > 0 && dot < s.Length - 1 && MediaExtensions.Contains(s[(dot + 1)..]))
            {
                steps.Add(new NormalizationStep("removed file extension", s[dot..]));
                s = s[..dot];
            }
        }

        // 2. Explicit provider identifier markers are kept as evidence, not discarded.
        s = ProviderMarkerRegex().Replace(s, m =>
        {
            var key = m.Groups["key"].Value.ToLowerInvariant() switch
            {
                "tmdbid" or "tmdb" => "Tmdb",
                "tvdbid" or "tvdb" => "Tvdb",
                "imdbid" or "imdb" => "Imdb",
                _ => m.Groups["key"].Value,
            };
            hints[key] = m.Groups["value"].Value;
            steps.Add(new NormalizationStep("kept provider marker as evidence", m.Value.Trim()));
            return " ";
        });

        // 3. Anchored site watermarks.
        s = rules.StripPrefixes(s, steps, warnings);
        s = TrimDebris(s);

        // 4. Find where the title ends.
        var hasStrongToken = false;
        string? episodeContext = null;
        int? year = null;
        var cut = s.Length;
        string? cutReason = null;

        var episode = FindEpisode(s);
        if (episode is { Index: > 0 } ep && HasTitleText(s[..ep.Index]))
        {
            cut = ep.Index;
            episodeContext = ep.Context;
            cutReason = "episode";
        }

        var release = FindFirstStrongReleaseToken(s);
        if (release > 0)
        {
            hasStrongToken = true;
            if (release < cut && HasTitleText(s[..release]))
            {
                cut = release;
                cutReason = "release";
            }
        }

        var yearMatch = FindYear(s, cut);
        if (yearMatch is { } ym)
        {
            year = ym.Year;
            if (ym.Index < cut)
            {
                cut = ym.Index;
                cutReason = "year";
            }

            steps.Add(new NormalizationStep("kept production year as evidence", ym.Year.ToString(CultureInfo.InvariantCulture)));
        }

        if (episodeContext is not null)
        {
            steps.Add(new NormalizationStep("extracted episode context", episodeContext));
        }

        var title = s[..cut];
        var suffix = s[cut..];
        if (cutReason is not null && HasTitleText(suffix))
        {
            var removed = TrimDebris(suffix);
            if (year is not null)
            {
                removed = YearRemovalRegex().Replace(removed, string.Empty, 1).Trim();
                removed = TrimDebris(removed);
            }

            if (episodeContext is not null && removed.StartsWith(episodeContext, StringComparison.OrdinalIgnoreCase))
            {
                removed = TrimDebris(removed[episodeContext.Length..]);
            }

            if (removed.Length > 0)
            {
                steps.Add(new NormalizationStep("removed release suffix", removed));
            }
        }

        // 5. Separators.
        title = NormalizeSeparators(title, steps);

        // 6. Scene-release context only: trailing edition tags and country qualifiers.
        var sceneName = hasStrongToken || episodeContext is not null;
        if (sceneName)
        {
            title = StripTrailingWeakTokens(title, steps);
            title = StripCountryQualifier(title, steps, warnings);
        }

        // 7. Debris left at the ends.
        title = FinalTrim(title);

        // 8. Validation.
        var isSafe = HasTitleText(title) && !IsOnlyReleaseToken(title);
        if (!isSafe)
        {
            warnings.Add("no usable title text remains after cleaning");
            title = string.Empty;
        }

        if (input.Source == NameSourceKind.EpisodeFile)
        {
            warnings.Add("title derived from an episode filename (fallback evidence)");
        }

        if (input.Kind == MediaKind.Movie && episodeContext is not null)
        {
            warnings.Add("film name contains an episode marker");
        }

        return new NormalizationResult(
            original,
            title,
            ComparisonKey(title),
            year,
            episodeContext,
            hints,
            steps,
            input.Source,
            isSafe,
            warnings);
    }

    private static bool HasTitleText(string s) => s.Any(char.IsLetterOrDigit);

    private static bool IsOnlyReleaseToken(string title)
    {
        var trimmed = title.Trim();
        var m = StrongReleaseTokenRegex().Match(trimmed);
        return m.Success && m.Index == 0 && m.Length == trimmed.Length;
    }

    private static string TrimDebris(string s)
    {
        var t = s.Trim().TrimStart('-', '_', '.', ':', '|', '~', ',', ';', '+', ' ', '\t');
        while (t.Length > 0)
        {
            var last = t[^1];
            if (last is '-' or '_' or ':' or '|' or '~' or ',' or ';' or '+' or ' ' or '\t'
                || (last == '.' && !EndsWithAcronymOrAbbreviation(t)))
            {
                t = t[..^1];
                continue;
            }

            break;
        }

        return t;
    }

    private static string FinalTrim(string s)
    {
        var t = WhitespaceRegex().Replace(s, " ");
        t = EmptyBracketRegex().Replace(t, " ");
        t = WhitespaceRegex().Replace(t, " ").Trim();

        // Leading debris, including an unmatched opening bracket.
        t = t.TrimStart('-', '_', '.', ':', '|', '~', ',', ';', '+', ' ');

        // Trailing debris. A trailing dot survives only when it closes an acronym or abbreviation.
        while (t.Length > 0)
        {
            var last = t[^1];
            if (last is '-' or '_' or ':' or '|' or '~' or ',' or ';' or '+' or ' ')
            {
                t = t[..^1];
                continue;
            }

            if (last == '.' && !EndsWithAcronymOrAbbreviation(t))
            {
                t = t[..^1];
                continue;
            }

            if (last is '(' or '[' or '{')
            {
                t = t[..^1];
                continue;
            }

            break;
        }

        t = BalanceBrackets(t);
        return t.Trim();
    }

    private static string BalanceBrackets(string t)
    {
        // Drop a dangling unmatched closing or opening bracket at either end (e.g. "Title)" after a cut).
        foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
        {
            var opens = t.Count(c => c == open);
            var closes = t.Count(c => c == close);
            if (closes > opens && t.EndsWith(close))
            {
                t = t[..^1].TrimEnd();
            }
            else if (opens > closes && t.StartsWith(open))
            {
                t = t[1..].TrimStart();
            }
            else if (opens > closes)
            {
                var idx = t.LastIndexOf(open);
                if (idx >= 0 && !t[(idx + 1)..].Contains(close, StringComparison.Ordinal))
                {
                    t = (t[..idx] + t[(idx + 1)..]).Trim();
                }
            }
        }

        return t;
    }

    private static bool EndsWithAcronymOrAbbreviation(string t)
    {
        if (AcronymEndRegex().IsMatch(t))
        {
            return true;
        }

        var lastSpace = t.LastIndexOf(' ');
        var lastWord = t[(lastSpace + 1)..].TrimEnd('.');
        return Abbreviations.Contains(lastWord);
    }

    private static (int Index, string Context)? FindEpisode(string s)
    {
        (int Index, string Context)? best = null;
        void Consider(Match m, string context)
        {
            if (m.Success && (best is null || m.Index < best.Value.Index))
            {
                best = (m.Index, context);
            }
        }

        var se = SeasonEpisodeRegex().Match(s);
        if (se.Success)
        {
            Consider(se, string.Create(CultureInfo.InvariantCulture, $"S{int.Parse(se.Groups["s"].Value, CultureInfo.InvariantCulture):00}E{int.Parse(se.Groups["e"].Value, CultureInfo.InvariantCulture):00}"));
        }

        var x = CrossEpisodeRegex().Match(s);
        if (x.Success)
        {
            Consider(x, string.Create(CultureInfo.InvariantCulture, $"{int.Parse(x.Groups["s"].Value, CultureInfo.InvariantCulture)}x{x.Groups["e"].Value}"));
        }

        var date = DateEpisodeRegex().Match(s);
        if (date.Success)
        {
            Consider(date, date.Value.Replace('.', '-').Replace('_', '-').Replace(' ', '-'));
        }

        var pack = SeasonPackRegex().Match(s);
        if (pack.Success)
        {
            Consider(pack, pack.Value.ToUpperInvariant().Replace('.', ' ').Replace('_', ' ').Trim());
        }

        var season = SeasonWordRegex().Match(s);
        if (season.Success)
        {
            Consider(season, season.Value.Replace('.', ' ').Replace('_', ' ').Trim());
        }

        return best;
    }

    private static int FindFirstStrongReleaseToken(string s)
    {
        var m = StrongReleaseTokenRegex().Match(s);
        while (m.Success)
        {
            var start = m.Index;

            // Include an opening bracket that wraps the token, e.g. "[1080p]".
            if (start > 0 && s[start - 1] is '[' or '(' or '{')
            {
                start--;
            }

            if (start > 0)
            {
                return start;
            }

            m = m.NextMatch();
        }

        return -1;
    }

    private (int Index, int Year)? FindYear(string full, int cut)
    {
        var maxYear = _maxYear();
        foreach (Match m in YearRegex().Matches(full))
        {
            if (m.Index >= cut)
            {
                break;
            }

            var year = int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
            if (year < 1880 || year > maxYear)
            {
                continue;
            }

            var before = full[..m.Index];
            if (!HasTitleText(before))
            {
                // "1917", "2012": the number is the title.
                continue;
            }

            var bracketed = m.Groups["open"].Success && m.Groups["close"].Success;
            var after = full[(m.Index + m.Length)..];
            if (bracketed || IsReleaseContext(after))
            {
                return (m.Index, year);
            }
        }

        return null;
    }

    private static bool IsReleaseContext(string after)
    {
        var rest = after.TrimStart('.', '_', ' ', '-', ')', ']', '}');
        if (rest.Length == 0)
        {
            return true;
        }

        if (rest[0] is '[' or '(' or '{')
        {
            return true;
        }

        var strong = StrongReleaseTokenRegex().Match(rest);
        if (strong.Success && strong.Index == 0)
        {
            return true;
        }

        var weak = WeakTokenRegex().Match(rest);
        if (weak.Success && weak.Index == 0)
        {
            return true;
        }

        var ep = SeasonEpisodeRegex().Match(rest);
        if (ep.Success && ep.Index == 0)
        {
            return true;
        }

        var pack = SeasonPackRegex().Match(rest);
        return pack.Success && pack.Index == 0;
    }

    private static string NormalizeSeparators(string title, List<NormalizationStep> steps)
    {
        var trimmed = TrimDebris(title);
        var hasWhitespace = trimmed.Any(char.IsWhiteSpace);
        var usesDots = DottedWordRegex().IsMatch(trimmed);
        var usesUnderscores = trimmed.Contains('_', StringComparison.Ordinal);

        if (usesUnderscores)
        {
            trimmed = trimmed.Replace('_', ' ');
            steps.Add(new NormalizationStep("replaced separators", "_ → space"));
            hasWhitespace = true;
        }

        if (usesDots && !hasWhitespace)
        {
            var rebuilt = RebuildDotted(trimmed);
            if (rebuilt != trimmed)
            {
                steps.Add(new NormalizationStep("replaced separators", ". → space"));
            }

            return rebuilt;
        }

        return trimmed;
    }

    private static string RebuildDotted(string s)
    {
        var tokens = s.Split('.', StringSplitOptions.None);
        var sb = new StringBuilder();
        var i = 0;
        while (i < tokens.Length)
        {
            var token = tokens[i];
            if (token.Length == 0)
            {
                i++;
                continue;
            }

            // Acronym: two or more single-letter tokens, e.g. S.H.I.E.L.D or A.I.
            if (token.Length == 1 && char.IsLetter(token[0]) && i + 1 < tokens.Length && tokens[i + 1].Length == 1 && char.IsLetter(tokens[i + 1][0]))
            {
                var j = i;
                var acronym = new StringBuilder();
                while (j < tokens.Length && tokens[j].Length == 1 && char.IsLetter(tokens[j][0]))
                {
                    acronym.Append(tokens[j]).Append('.');
                    j++;
                }

                Append(sb, acronym.ToString());
                i = j;
                continue;
            }

            // Decimal such as "2.0" in "Tron.2.0".
            if (token.Length <= 2 && token.All(char.IsDigit) && i + 1 < tokens.Length && tokens[i + 1].Length == 1 && char.IsDigit(tokens[i + 1][0]))
            {
                Append(sb, token + "." + tokens[i + 1]);
                i += 2;
                continue;
            }

            if (Abbreviations.Contains(token) && i + 1 < tokens.Length)
            {
                Append(sb, token + ".");
                i++;
                continue;
            }

            Append(sb, token);
            i++;
        }

        return sb.ToString();

        static void Append(StringBuilder sb, string value)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(value);
        }
    }

    private static string StripTrailingWeakTokens(string title, List<NormalizationStep> steps)
    {
        var current = title.TrimEnd();
        for (var guard = 0; guard < 4; guard++)
        {
            var m = TrailingWeakTokenRegex().Match(current);
            if (!m.Success)
            {
                break;
            }

            var remaining = current[..m.Index].TrimEnd(' ', '-', '.', '_');
            var words = remaining.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.All(w => Articles.Contains(w)))
            {
                break;
            }

            steps.Add(new NormalizationStep("removed edition tag", m.Value.Trim()));
            current = remaining;
        }

        return current;
    }

    private static string StripCountryQualifier(string title, List<NormalizationStep> steps, List<string> warnings)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return title;
        }

        var last = words[^1].Trim('(', ')', '[', ']');
        if (!CountryQualifiers.Contains(last, StringComparer.Ordinal))
        {
            return title;
        }

        steps.Add(new NormalizationStep("separated country qualifier", last));
        warnings.Add($"country qualifier '{last}' kept as evidence only; edition is not assumed");
        return string.Join(' ', words[..^1]);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")]
    private static partial Regex ControlCharRegex();

    [GeneratedRegex(@"[\[\{\(]\s*(?<key>tmdbid|tvdbid|imdbid|tmdb|tvdb|imdb)\s*[-=]\s*(?<value>[A-Za-z0-9]{1,20})\s*[\]\}\)]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProviderMarkerRegex();

    [GeneratedRegex(@"\(\s*\)|\[\s*\]|\{\s*\}")]
    private static partial Regex EmptyBracketRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])S(?<s>\d{1,2})[ ._-]?E(?<e>\d{1,4})(?:(?:[ ._-]?E|-)\d{1,4})*(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<s>\d{1,2})x(?<e>\d{2,3})(?![0-9A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CrossEpisodeRegex();

    [GeneratedRegex(@"(?<![0-9])(?:19|20)\d{2}[ ._-](?:0[1-9]|1[0-2])[ ._-](?:0[1-9]|[12]\d|3[01])(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex DateEpisodeRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])S\d{1,2}(?:[ ._]?-[ ._]?S?\d{1,2})?(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonPackRegex();

    [GeneratedRegex(@"(?<![A-Za-z])Seasons?[ ._-]?\d{1,2}(?:[ ._]?(?:-|to)[ ._]?\d{1,2})?(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonWordRegex();

    [GeneratedRegex(@"(?<open>[\(\[\{])?(?<![0-9A-Za-z])(?<year>(?:18|19|20)\d{2})(?![0-9A-Za-z])(?<close>[\)\]\}])?", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"^[\(\[\{]?(?:18|19|20)\d{2}[\)\]\}]?", RegexOptions.CultureInvariant)]
    private static partial Regex YearRemovalRegex();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:\d{3,4}[pi]|4k|uhd|blu-?ray|bd-?rip|br-?rip|bd-?remux|remux|web-?dl|web-?rip|hdtv|pdtv|dvd-?rip|dvd-?scr|dvdr|hd-?rip|hdcam|cam-?rip|telesync|hd-?ts|tv-?rip|vhs-?rip|[xh]\.?26[45]|hevc|avc|xvid|divx|av1|vp9|10-?bit|8-?bit|hi10p|aac(?:[257]\.[01])?|ac3|e-?ac-?3|dts(?:-?hd)?(?:-?ma)?|truehd|atmos|flac|ddp?[257]\.[01]|dd\+|[57]\.1|hdr10\+?|hdr|dolby[ ._-]?vision|dovi|repack|rerip|amzn|dsnp|hmax|atvp)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrongReleaseTokenRegex();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:the[ ._-]complete[ ._-]series|complete[ ._-]series|complete|extended(?:[ ._-](?:cut|edition))?|theatrical(?:[ ._-]cut)?|unrated|remastered|uncut|directors[ ._-]?cut|imax|limited|multi(?:subs)?|subbed|dubbed|vostfr|truefrench|french|german|ita|eng|web|internal)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeakTokenRegex();

    [GeneratedRegex(
        @"[ ._-](?:the[ ._-]complete[ ._-]series|complete[ ._-]series|complete|extended(?:[ ._-](?:cut|edition))?|theatrical(?:[ ._-]cut)?|unrated|remastered|uncut|directors[ ._-]?cut|imax|limited|multi(?:subs)?|subbed|dubbed|vostfr|truefrench|french|german|ita|eng|web|internal)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingWeakTokenRegex();

    [GeneratedRegex(@"[\p{L}\p{N}'’]\.[\p{L}\p{N}]")]
    private static partial Regex DottedWordRegex();

    [GeneratedRegex(@"(?:(?<![\p{L}])\p{L}\.){2,}$")]
    private static partial Regex AcronymEndRegex();
}
