using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SmartMatch.Normalization;

/// <summary>
/// An anchored rule that removes a release-site watermark from the start of a name.
/// </summary>
/// <param name="Id">Stable identifier shown in traces.</param>
/// <param name="Description">Friendly explanation.</param>
/// <param name="Pattern">Regular expression; always evaluated anchored at the start of the name.</param>
/// <param name="Example">Example input.</param>
/// <param name="ExampleOutput">Expected remainder for <paramref name="Example"/> after the prefix is removed.</param>
/// <param name="BuiltIn">True for rules shipped with the plugin.</param>
public sealed record PrefixRule(string Id, string Description, string Pattern, string Example, string ExampleOutput, bool BuiltIn);

/// <summary>
/// A validated, compiled set of prefix rules.
/// </summary>
public sealed class RuleSet
{
    /// <summary>Maximum accepted length of a user-supplied pattern.</summary>
    public const int MaxPatternLength = 200;

    /// <summary>Regex evaluation timeout. Protects the worker from catastrophic backtracking.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly List<(PrefixRule Rule, Regex Regex)> _compiled;

    private RuleSet(List<(PrefixRule Rule, Regex Regex)> compiled, string revision)
    {
        _compiled = compiled;
        Revision = revision;
    }

    /// <summary>Gets the default rule set (built-in rules only).</summary>
    public static RuleSet Default { get; } = Create(Array.Empty<PrefixRule>());

    /// <summary>Gets a short hash identifying the active rules. Part of every finding fingerprint.</summary>
    public string Revision { get; }

    /// <summary>Gets the active rules in evaluation order.</summary>
    public IEnumerable<PrefixRule> Rules => _compiled.Select(c => c.Rule);

    /// <summary>
    /// Builds a rule set from the built-in rules plus enabled, validated user rules.
    /// Invalid user rules are skipped; configuration validation reports them separately.
    /// </summary>
    /// <param name="userRules">Enabled user rules.</param>
    /// <returns>The compiled rule set.</returns>
    public static RuleSet Create(IEnumerable<PrefixRule> userRules)
    {
        var compiled = new List<(PrefixRule, Regex)>();
        foreach (var rule in BuiltInPrefixRules.All)
        {
            compiled.Add((rule, Compile(rule.Pattern)));
        }

        foreach (var rule in userRules)
        {
            if (TryCompile(rule.Pattern, out var regex, out _))
            {
                compiled.Add((rule, regex!));
            }
        }

        var revisionSource = string.Join('\n', compiled.Select(c => c.Item1.Id + "=" + c.Item1.Pattern));
        var revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(revisionSource)))[..12];
        return new RuleSet(compiled, revision.ToLowerInvariant());
    }

    /// <summary>
    /// Validates and compiles a user pattern. The pattern is wrapped so it can only match at the start of a name.
    /// </summary>
    /// <param name="pattern">User pattern.</param>
    /// <param name="regex">Compiled regex when valid.</param>
    /// <param name="error">Error description when invalid.</param>
    /// <returns>True when valid.</returns>
    public static bool TryCompile(string? pattern, out Regex? regex, out string? error)
    {
        regex = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Pattern is empty.";
            return false;
        }

        if (pattern.Length > MaxPatternLength)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Pattern is longer than {MaxPatternLength} characters.");
            return false;
        }

        try
        {
            regex = Compile(pattern);
        }
        catch (ArgumentException ex)
        {
            error = "Invalid regular expression: " + ex.Message;
            return false;
        }

        // A rule that can match the empty string would "remove" nothing but still report a step.
        try
        {
            if (regex.IsMatch(string.Empty))
            {
                regex = null;
                error = "Pattern matches an empty name; it must require at least one character.";
                return false;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            regex = null;
            error = "Pattern timed out on an empty input.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Removes at most a few stacked site prefixes from the start of <paramref name="name"/>.
    /// </summary>
    /// <param name="name">Name to clean.</param>
    /// <param name="steps">Trace sink.</param>
    /// <param name="warnings">Warning sink.</param>
    /// <returns>The remainder.</returns>
    public string StripPrefixes(string name, List<NormalizationStep> steps, List<string> warnings)
    {
        var current = name;
        for (var pass = 0; pass < 3; pass++)
        {
            var changed = false;
            foreach (var (rule, regex) in _compiled)
            {
                Match match;
                try
                {
                    match = regex.Match(current);
                }
                catch (RegexMatchTimeoutException)
                {
                    warnings.Add($"prefix rule {rule.Id} timed out and was skipped");
                    continue;
                }

                if (!match.Success || match.Length == 0 || match.Length >= current.Length)
                {
                    continue;
                }

                var removed = current[..match.Length];
                var remainder = current[match.Length..];
                if (!remainder.Any(char.IsLetterOrDigit))
                {
                    continue;
                }

                steps.Add(new NormalizationStep("removed site-prefix", removed.Trim().TrimEnd('-', '_', '.', ':', '|', '~').Trim(), rule.Id));
                var separator = removed[removed.TrimEnd().TrimEnd('-', '_', '.', ':', '|', '~', ' ').Length..].Trim();
                if (separator.Length > 0)
                {
                    steps.Add(new NormalizationStep("trimmed separator", separator));
                }

                current = remainder;
                changed = true;
                break;
            }

            if (!changed)
            {
                break;
            }
        }

        return current;
    }

    private static Regex Compile(string pattern)
        => new("^(?:" + pattern + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
}

/// <summary>
/// Version-controlled built-in watermark rules. Each one is covered by fixtures in the unit tests.
/// Rules are deliberately specific: a domain is only removed when it is unmistakably a watermark
/// (a known site, a "www." host followed by a dash, or a bracketed host at the very start).
/// </summary>
public static class BuiltInPrefixRules
{
    private const string Separator = @"\s*(?:[-_.:|~]+\s*|\s+)";

    private const string Tld = "(?:com|org|net|to|io|me|cc|tv|info|xyz|ws|se|ru|lol|club|biz|co|nl|ch|li|si|vip|cx|pw|eu|top|site|online|mx|in|uk|cz|sx|ag|am|rs|is|gg|lt|re|link|fun|pro|ms|ph|pm|nz|ac|ist)";

    /// <summary>Gets all built-in rules.</summary>
    public static IReadOnlyList<PrefixRule> All { get; } = new[]
    {
        new PrefixRule(
            "site.uindex",
            "UIndex release-site watermark",
            @"\s*[\[({]?\s*(?:www\.)?uindex\.org\s*[\])}]?" + Separator,
            "www.UIndex.org - The Fall and Rise of Reggie Dinkins",
            "The Fall and Rise of Reggie Dinkins",
            true),
        new PrefixRule(
            "site.known",
            "Well-known torrent/release index domains",
            @"\s*[\[({]?\s*(?:www\.)?(?:1337x\.to|rarbg\.to|yts\.(?:mx|am|lt|ag|rs)|eztv\.(?:re|io|ag|wf)|torrentgalaxy\.to|thepiratebay\.org|torrenting\.com|torrentday\.com|rutracker\.org|1tamilmv\.[a-z]{2,6}|tamilrockers\.[a-z]{2,6}|mkvcinemas\.[a-z]{2,6}|katmoviehd\.[a-z]{2,6}|cpasbien\.[a-z]{2,6}|oxtorrent\.[a-z]{2,6}|torrent9\.[a-z]{2,6}|zooqle\.com|limetorrents\.[a-z]{2,6}|ettvcentral\.com|glodls\.to)\s*[\])}]?" + Separator,
            "[ www.Torrenting.com ] - Movie.Title.2024.1080p",
            "Movie.Title.2024.1080p",
            true),
        new PrefixRule(
            "site.www-dash",
            "Any www.<host> watermark followed by a dash",
            @"\s*www\.[a-z0-9-]+(?:\.[a-z0-9-]+)*\." + Tld + @"\s+-+\s*",
            "www.ExampleSite.com - Some Title",
            "Some Title",
            true),
        new PrefixRule(
            "site.bracketed",
            "Bracketed site domain at the start of a name",
            @"\s*[\[({【]\s*(?:www\.)?[a-z0-9-]+(?:\.[a-z0-9-]+)*\." + Tld + @"\s*[\])}】]\s*(?:[-_.:|~]+\s*)?",
            "[ExampleSite.org] Some Title",
            "Some Title",
            true),
    };
}
