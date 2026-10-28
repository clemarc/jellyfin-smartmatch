using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartMatch.Normalization;

/// <summary>
/// The kind of library record a name belongs to.
/// </summary>
public enum MediaKind
{
    /// <summary>A TV series (folder record).</summary>
    Series,

    /// <summary>A film.</summary>
    Movie,
}

/// <summary>
/// Where a raw name came from. Recorded so the admin can see the provenance of a search title.
/// </summary>
public enum NameSourceKind
{
    /// <summary>Basename of a series folder.</summary>
    SeriesFolder,

    /// <summary>Basename of a film's containing folder.</summary>
    MovieFolder,

    /// <summary>A film's media filename.</summary>
    MovieFile,

    /// <summary>An episode filename used as fallback evidence for its series.</summary>
    EpisodeFile,

    /// <summary>The current Jellyfin display name.</summary>
    Metadata,

    /// <summary>Free text typed into a preview box.</summary>
    Preview,
}

/// <summary>
/// Input to the normalizer.
/// </summary>
/// <param name="RawName">Basename or display name. Never a full path.</param>
/// <param name="Source">Provenance of the name.</param>
/// <param name="Kind">Media kind being identified.</param>
public sealed record MediaNameInput(string RawName, NameSourceKind Source, MediaKind Kind)
{
    /// <summary>
    /// Gets a value indicating whether the name is a filename whose known media extension should be removed.
    /// Folder names are never treated as having an extension, so "Mr.Robot" keeps its dot-separated words.
    /// </summary>
    public bool IsFile => Source is NameSourceKind.MovieFile or NameSourceKind.EpisodeFile;
}

/// <summary>
/// One cleaning operation, shown to the admin as e.g. "removed site-prefix: www.UIndex.org".
/// </summary>
/// <param name="Operation">Stable operation code.</param>
/// <param name="Detail">The text affected.</param>
/// <param name="RuleId">Rule identifier when a prefix rule was applied.</param>
public sealed record NormalizationStep(string Operation, string Detail, string? RuleId = null)
{
    /// <inheritdoc />
    public override string ToString() => RuleId is null ? $"{Operation}: {Detail}" : $"{Operation} ({RuleId}): {Detail}";
}

/// <summary>
/// Output of the normalizer.
/// </summary>
/// <param name="OriginalName">Input name unchanged.</param>
/// <param name="SearchTitle">Human-readable title sent to metadata providers.</param>
/// <param name="ComparisonKey">Casefolded, punctuation-insensitive key used for equivalence checks.</param>
/// <param name="TrustedYear">Production year when the name carries one unambiguously.</param>
/// <param name="EpisodeContext">Extracted episode marker such as S01E01, kept out of the search title.</param>
/// <param name="ProviderHints">Explicit provider identifier markers such as [tmdbid-123].</param>
/// <param name="Steps">Trace of applied operations.</param>
/// <param name="Source">Provenance of the name.</param>
/// <param name="IsSafe">False when no usable title remains.</param>
/// <param name="Warnings">Evidence the admin should see; any warning makes automation ineligible.</param>
public sealed record NormalizationResult(
    string OriginalName,
    string SearchTitle,
    string ComparisonKey,
    int? TrustedYear,
    string? EpisodeContext,
    IReadOnlyDictionary<string, string> ProviderHints,
    IReadOnlyList<NormalizationStep> Steps,
    NameSourceKind Source,
    bool IsSafe,
    IReadOnlyList<string> Warnings);
