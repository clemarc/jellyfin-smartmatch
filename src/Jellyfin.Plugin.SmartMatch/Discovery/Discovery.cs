using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartMatch.Configuration;
using Jellyfin.Plugin.SmartMatch.Library;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Normalization;

namespace Jellyfin.Plugin.SmartMatch.Discovery;

/// <summary>
/// Eligibility decision for one item.
/// </summary>
/// <param name="Kind">Process, Skip or Defer.</param>
/// <param name="Code">Reason code (MISSING, LOCKED, IGNORED, BUSY, ALREADY_IDENTIFIED, ...).</param>
/// <param name="Message">Explanation.</param>
public sealed record Eligibility(EligibilityKind Kind, string Code, string Message)
{
    /// <summary>Gets the "process" decision.</summary>
    public static Eligibility Process { get; } = new(EligibilityKind.Process, "ELIGIBLE", "Item needs investigation.");
}

/// <summary>
/// Eligibility outcome kinds.
/// </summary>
public enum EligibilityKind
{
    /// <summary>Normalize and search.</summary>
    Process,

    /// <summary>Do not process.</summary>
    Skip,

    /// <summary>Try again later.</summary>
    Defer,
}

/// <summary>
/// Implements the eligibility algorithm of TECHNICAL_DESIGN.md §5.
/// </summary>
public static class ItemEligibilityEvaluator
{
    /// <summary>
    /// Evaluates an item.
    /// </summary>
    /// <param name="item">Fresh snapshot.</param>
    /// <param name="inScope">True when the item's library is in scope.</param>
    /// <param name="existing">Existing finding, if any.</param>
    /// <param name="validator">Provider id validator.</param>
    /// <param name="busy">True while Jellyfin refreshes the item.</param>
    /// <returns>Decision.</returns>
    public static Eligibility Evaluate(ItemSnapshot item, bool inScope, Finding? existing, ProviderIdValidator validator, bool busy)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(validator);

        if (item.IsVirtual || string.IsNullOrEmpty(item.Path))
        {
            return new Eligibility(EligibilityKind.Skip, "MISSING", "Item is virtual or has no media path.");
        }

        if (!inScope)
        {
            return new Eligibility(EligibilityKind.Skip, "NOT_IN_SCOPE", "Library is not selected for Smart Match.");
        }

        if (item.IsLocked)
        {
            return new Eligibility(EligibilityKind.Skip, "LOCKED", "Item metadata is locked.");
        }

        if (existing?.Status == FindingStatus.Ignored)
        {
            return new Eligibility(EligibilityKind.Skip, "IGNORED", "Admin ignored this item.");
        }

        if (busy)
        {
            return new Eligibility(EligibilityKind.Defer, "BUSY", "Jellyfin is refreshing this item.");
        }

        if (validator.HasReliableIdentity(item.ProviderIds))
        {
            return new Eligibility(EligibilityKind.Skip, "ALREADY_IDENTIFIED", "Item already has a provider identity.");
        }

        if (existing?.Status == FindingStatus.Rejected)
        {
            return new Eligibility(EligibilityKind.Skip, "REJECTED", "Admin rejected the candidates; use Retry to search again.");
        }

        if (existing?.Status is FindingStatus.Applying or FindingStatus.Partial or FindingStatus.RolledBack)
        {
            return new Eligibility(EligibilityKind.Skip, "PLUGIN_OWNED", "A plugin application is in progress or awaiting admin action.");
        }

        return Eligibility.Process;
    }
}

/// <summary>
/// Chooses the name evidence for an item and normalizes it.
/// </summary>
public static class NameEvidence
{
    private static readonly System.Buffers.SearchValues<char> Separators = System.Buffers.SearchValues.Create("/\\");

    /// <summary>
    /// Builds the normalization used for searching.
    /// </summary>
    /// <param name="item">Snapshot.</param>
    /// <param name="normalizer">Normalizer.</param>
    /// <param name="rules">Rules.</param>
    /// <param name="episodeFallback">Lazily returns an episode filename for a series.</param>
    /// <returns>The chosen normalization and the basename it came from.</returns>
    public static (NormalizationResult Result, string SourceName) Build(ItemSnapshot item, IMediaNameNormalizer normalizer, RuleSet rules, Func<string?> episodeFallback)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(episodeFallback);

        var sources = new List<MediaNameInput>();
        var kind = item.Kind;
        var path = item.Path ?? string.Empty;

        if (kind == MediaKind.Series)
        {
            var folder = Basename(path);
            if (folder.Length > 0)
            {
                sources.Add(new MediaNameInput(folder, NameSourceKind.SeriesFolder, kind));
            }
        }
        else if (item.PathIsDirectory)
        {
            var folder = Basename(path);
            if (folder.Length > 0)
            {
                sources.Add(new MediaNameInput(folder, NameSourceKind.MovieFolder, kind));
            }
        }
        else
        {
            var file = Basename(path);
            var folder = Basename(ParentDirectory(path));
            if (!item.IsInMixedFolder && folder.Length > 0)
            {
                sources.Add(new MediaNameInput(folder, NameSourceKind.MovieFolder, kind));
            }

            if (file.Length > 0)
            {
                sources.Add(new MediaNameInput(file, NameSourceKind.MovieFile, kind));
            }
        }

        if (sources.Count == 0 && !string.IsNullOrWhiteSpace(item.Name))
        {
            sources.Add(new MediaNameInput(item.Name, NameSourceKind.Metadata, kind));
        }

        var results = sources.Select(s => (Result: normalizer.Normalize(s, rules), Name: s.RawName)).ToList();
        var primary = results.FirstOrDefault(r => r.Result.IsSafe);

        if (primary.Result is null && kind == MediaKind.Series)
        {
            var episode = episodeFallback();
            if (!string.IsNullOrWhiteSpace(episode))
            {
                var r = normalizer.Normalize(new MediaNameInput(episode, NameSourceKind.EpisodeFile, kind), rules);
                if (r.IsSafe)
                {
                    return (r, episode);
                }
            }
        }

        if (primary.Result is null)
        {
            var first = results.FirstOrDefault();
            return first.Result is null
                ? (normalizer.Normalize(new MediaNameInput(item.Name, NameSourceKind.Metadata, kind), rules), item.Name)
                : (first.Result, first.Name);
        }

        var chosen = primary.Result;
        var usedFallback = results.IndexOf(primary) > 0;

        // Corroborate the year with the other source (e.g. film folder vs file).
        foreach (var other in results.Where(r => r.Result.IsSafe && !ReferenceEquals(r.Result, chosen)))
        {
            var similar = TitleSimilarity.Compute(chosen.ComparisonKey, other.Result.ComparisonKey) >= 0.85;
            if (!similar)
            {
                continue;
            }

            if (chosen.TrustedYear is null && other.Result.TrustedYear is not null)
            {
                chosen = chosen with
                {
                    TrustedYear = other.Result.TrustedYear,
                    Steps = chosen.Steps.Append(new NormalizationStep("year corroborated from " + Describe(other.Result.Source), other.Result.TrustedYear.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList(),
                };
            }
            else if (chosen.TrustedYear is not null && other.Result.TrustedYear is not null && chosen.TrustedYear != other.Result.TrustedYear)
            {
                chosen = chosen with
                {
                    TrustedYear = null,
                    Warnings = chosen.Warnings.Append("folder and file name disagree on the year").ToList(),
                };
            }
        }

        if (usedFallback)
        {
            chosen = chosen with { Warnings = chosen.Warnings.Append("primary name was unusable; used " + Describe(chosen.Source)).ToList() };
        }

        return (chosen, primary.Name);
    }

    /// <summary>Returns a basename for a path that may use either separator.</summary>
    /// <param name="path">Path.</param>
    /// <returns>Basename (never with an extension removed).</returns>
    public static string Basename(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd('/', '\\');
        var idx = trimmed.AsSpan().LastIndexOfAny(Separators);
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static string ParentDirectory(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var idx = trimmed.AsSpan().LastIndexOfAny(Separators);
        return idx > 0 ? trimmed[..idx] : string.Empty;
    }

    private static string Describe(NameSourceKind kind) => kind switch
    {
        NameSourceKind.SeriesFolder => "series folder",
        NameSourceKind.MovieFolder => "film folder",
        NameSourceKind.MovieFile => "film filename",
        NameSourceKind.EpisodeFile => "episode filename",
        _ => "metadata name",
    };
}

/// <summary>
/// Library scope helpers.
/// </summary>
public static class LibraryScope
{
    /// <summary>
    /// Returns the libraries in scope according to configuration.
    /// </summary>
    /// <param name="libraries">All supported libraries.</param>
    /// <param name="config">Configuration.</param>
    /// <returns>In-scope library ids.</returns>
    public static HashSet<Guid> InScope(IEnumerable<LibraryInfo> libraries, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var all = libraries.Select(l => l.Id).ToHashSet();
        if (config.IncludeAllLibraries)
        {
            return all;
        }

        var selected = (config.IncludedLibraryIds ?? Array.Empty<string>()).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).ToHashSet();
        all.IntersectWith(selected);
        return all;
    }

    /// <summary>
    /// Returns true when automatic application is enabled for a library right now.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <param name="libraryId">Library id.</param>
    /// <returns>True when enabled.</returns>
    public static bool AutoApplyEnabled(PluginConfiguration config, Guid libraryId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Mode == ApplyMode.AutoApplyHighConfidence
            && (config.AutoApplyLibraryIds ?? Array.Empty<string>()).Any(s => Guid.TryParse(s, out var g) && g == libraryId);
    }

    /// <summary>
    /// Returns true when the admin accepted sidecar writes for a library.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <param name="libraryId">Library id.</param>
    /// <returns>True when acknowledged.</returns>
    public static bool SidecarAcknowledged(PluginConfiguration config, Guid libraryId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return (config.SidecarAcknowledgedLibraryIds ?? Array.Empty<string>()).Any(s => Guid.TryParse(s, out var g) && g == libraryId);
    }
}
