using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SmartMatch.Normalization;

namespace Jellyfin.Plugin.SmartMatch.Matching;

/// <summary>
/// Decides which provider identifiers are trustworthy identities and which may be written.
/// Unknown namespaces are never written: an arbitrary key from a search result is dropped
/// rather than stored on the item.
/// </summary>
public sealed partial class ProviderIdValidator
{
    private static readonly Dictionary<string, Regex> KnownFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tmdb"] = NumericId(),
        ["Tvdb"] = NumericId(),
        ["TvMaze"] = NumericId(),
        ["Imdb"] = ImdbId(),
    };

    // Keys that mark an item as already identified. Includes keys written by common third-party
    // providers so the plugin never second-guesses their identification.
    private static readonly string[] IdentityKeys =
    {
        "Tmdb", "Tvdb", "Imdb", "TvMaze", "Tvcom", "Zap2It", "TvRage", "AniDB", "AniList", "AniSearch", "Kitsu",
    };

    private readonly HashSet<string> _extraWritable;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderIdValidator"/> class.
    /// </summary>
    /// <param name="extraWritableKeys">Admin-configured additional provider keys that may be written.</param>
    public ProviderIdValidator(IEnumerable<string>? extraWritableKeys = null)
    {
        _extraWritable = new HashSet<string>(
            (extraWritableKeys ?? Array.Empty<string>()).Where(IsValidKeyName),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates the shape of a provider key name.
    /// </summary>
    /// <param name="key">Key name.</param>
    /// <returns>True when acceptable.</returns>
    public static bool IsValidKeyName(string? key) => !string.IsNullOrWhiteSpace(key) && KeyName().IsMatch(key);

    /// <summary>
    /// Returns the provider keys the plugin may write for a media kind.
    /// </summary>
    /// <param name="kind">Media kind.</param>
    /// <returns>Key names.</returns>
    public IReadOnlyCollection<string> WritableKeys(MediaKind kind)
    {
        var keys = kind == MediaKind.Series
            ? new List<string> { "Tvdb", "Tmdb", "Imdb", "TvMaze" }
            : new List<string> { "Tmdb", "Imdb", "Tvdb" };
        keys.AddRange(_extraWritable);
        return keys;
    }

    /// <summary>
    /// Returns true when the item already carries a reliable identity.
    /// </summary>
    /// <param name="providerIds">Current provider identifiers of the item.</param>
    /// <returns>True when identified.</returns>
    public bool HasReliableIdentity(IReadOnlyDictionary<string, string>? providerIds)
    {
        if (providerIds is null)
        {
            return false;
        }

        foreach (var (key, value) in providerIds)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var isIdentityKey = IdentityKeys.Contains(key, StringComparer.OrdinalIgnoreCase) || _extraWritable.Contains(key);
            if (!isIdentityKey)
            {
                continue;
            }

            if (KnownFormats.TryGetValue(key, out var format) && !format.IsMatch(value.Trim()))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Filters a search result's identifiers down to the validated, writable subset with canonical key casing.
    /// </summary>
    /// <param name="kind">Media kind.</param>
    /// <param name="providerIds">Identifiers reported by a provider.</param>
    /// <returns>Writable identifiers; empty when the candidate cannot be applied.</returns>
    public Dictionary<string, string> FilterWritable(MediaKind kind, IReadOnlyDictionary<string, string>? providerIds)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (providerIds is null)
        {
            return result;
        }

        var writable = WritableKeys(kind);
        foreach (var (key, rawValue) in providerIds)
        {
            var canonical = writable.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (canonical is null || string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var value = rawValue.Trim();
            if (KnownFormats.TryGetValue(canonical, out var format))
            {
                if (!format.IsMatch(value))
                {
                    continue;
                }
            }
            else if (!ExtraValue().IsMatch(value))
            {
                continue;
            }

            result[canonical] = value;
        }

        return result;
    }

    /// <summary>
    /// Builds a stable candidate identifier: the highest-priority writable key and its value.
    /// </summary>
    /// <param name="kind">Media kind.</param>
    /// <param name="writable">Writable identifiers.</param>
    /// <param name="fallbackName">Name used when no identifier exists.</param>
    /// <param name="fallbackYear">Year used when no identifier exists.</param>
    /// <returns>Candidate identifier.</returns>
    public string CandidateId(MediaKind kind, IReadOnlyDictionary<string, string> writable, string fallbackName, int? fallbackYear)
    {
        foreach (var key in WritableKeys(kind))
        {
            if (writable.TryGetValue(key, out var value))
            {
                return key.ToLowerInvariant() + ":" + value;
            }
        }

        return "name:" + MediaNameNormalizer.ComparisonKey(fallbackName).Replace(' ', '-') + (fallbackYear is null ? string.Empty : "-" + fallbackYear.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^\d{1,10}$")]
    private static partial Regex NumericId();

    [GeneratedRegex(@"^tt\d{5,10}$")]
    private static partial Regex ImdbId();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{1,31}$")]
    private static partial Regex KeyName();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ExtraValue();
}
