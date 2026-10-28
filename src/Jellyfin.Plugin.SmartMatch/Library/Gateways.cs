using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Normalization;

namespace Jellyfin.Plugin.SmartMatch.Library;

/// <summary>
/// A Jellyfin library as the plugin sees it.
/// </summary>
/// <param name="Id">Collection folder id.</param>
/// <param name="Name">Library name.</param>
/// <param name="CollectionType">movies, tvshows, or null for mixed.</param>
/// <param name="SidecarRisk">True when Jellyfin is configured to save metadata or images next to media.</param>
/// <param name="SidecarDetail">Human-readable explanation of the saver configuration.</param>
public sealed record LibraryInfo(Guid Id, string Name, string? CollectionType, bool SidecarRisk, string SidecarDetail);

/// <summary>
/// Read-only snapshot of a Series or Movie record. Taken fresh before every decision.
/// </summary>
public sealed record ItemSnapshot
{
    /// <summary>Gets the item id.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the media kind.</summary>
    public MediaKind Kind { get; init; }

    /// <summary>Gets the library id (empty when the item is outside any library).</summary>
    public Guid LibraryId { get; init; }

    /// <summary>Gets the library name.</summary>
    public string LibraryName { get; init; } = string.Empty;

    /// <summary>Gets the current display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the media path (folder for series and disc folders).</summary>
    public string? Path { get; init; }

    /// <summary>Gets a value indicating whether <see cref="Path"/> is a directory.</summary>
    public bool PathIsDirectory { get; init; }

    /// <summary>Gets a value indicating whether a film shares its folder with other films.</summary>
    public bool IsInMixedFolder { get; init; }

    /// <summary>Gets the production year.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the provider ids.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets a value indicating whether the whole item is locked.</summary>
    public bool IsLocked { get; init; }

    /// <summary>Gets a value indicating whether the Name field is locked.</summary>
    public bool NameLocked { get; init; }

    /// <summary>Gets a value indicating whether the item is virtual or missing.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>Gets a value indicating whether a local NFO exists for the item.</summary>
    public bool HasLocalNfo { get; init; }

    /// <summary>Gets the last time Jellyfin saved the item.</summary>
    public DateTime DateLastSaved { get; init; }
}

/// <summary>
/// Options for the post-apply refresh.
/// </summary>
/// <param name="ReplaceUnlockedText">Replace unlocked text metadata instead of only filling missing values.</param>
/// <param name="CandidateName">Selected candidate name (passed to Jellyfin as the identify search result).</param>
/// <param name="CandidateYear">Selected candidate year.</param>
/// <param name="ProviderIds">Selected provider ids.</param>
/// <param name="ProviderName">Provider that returned the candidate.</param>
public sealed record RefreshRequest(bool ReplaceUnlockedText, string CandidateName, int? CandidateYear, IReadOnlyDictionary<string, string> ProviderIds, string ProviderName);

/// <summary>
/// All plugin access to the Jellyfin library goes through this boundary.
/// </summary>
public interface ILibraryGateway
{
    /// <summary>Gets a value indicating whether a full library scan is running.</summary>
    bool IsLibraryScanRunning { get; }

    /// <summary>Gets Movies, Shows and Mixed libraries.</summary>
    /// <returns>Libraries.</returns>
    IReadOnlyList<LibraryInfo> GetLibraries();

    /// <summary>Takes a fresh snapshot of a Series or Movie.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Snapshot, or null when the item no longer exists or is not a Series/Movie.</returns>
    ItemSnapshot? GetItem(Guid itemId);

    /// <summary>Lists Series and Movie ids in the given libraries, optionally only those saved since a time.</summary>
    /// <param name="libraryIds">Libraries.</param>
    /// <param name="changedSinceUtc">Lower bound on DateLastSaved.</param>
    /// <returns>Item ids.</returns>
    IReadOnlyList<Guid> GetCandidateItemIds(IReadOnlyCollection<Guid> libraryIds, DateTime? changedSinceUtc);

    /// <summary>Returns the basename of one episode file of a series (fallback evidence).</summary>
    /// <param name="seriesId">Series id.</param>
    /// <returns>Basename or null.</returns>
    string? GetFallbackEpisodeFileName(Guid seriesId);

    /// <summary>Returns true while Jellyfin is refreshing the item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>True when busy.</returns>
    bool IsItemBusy(Guid itemId);

    /// <summary>Persists provider ids (and optionally the title) through Jellyfin's item update API.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="setProviderIds">Provider ids to set.</param>
    /// <param name="removeProviderKeys">Provider keys to remove (rollback).</param>
    /// <param name="title">New title, or null to leave it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task WriteIdentityAsync(Guid itemId, IReadOnlyDictionary<string, string> setProviderIds, IReadOnlyCollection<string> removeProviderKeys, string? title, CancellationToken cancellationToken);

    /// <summary>Runs Jellyfin's metadata refresh for an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="request">Refresh request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task RefreshAsync(Guid itemId, RefreshRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One enabled remote search provider for one item.
/// </summary>
/// <param name="Name">Provider name.</param>
/// <param name="SearchAsync">Search delegate (title, year, token).</param>
public sealed record SearchProviderHandle(string Name, Func<string, int?, CancellationToken, Task<IReadOnlyList<ProviderCandidate>>> SearchAsync);

/// <summary>
/// The enabled search providers for an item plus a fingerprint of that configuration.
/// </summary>
/// <param name="Providers">Enabled remote search providers in library order.</param>
/// <param name="Fingerprint">Fingerprint of providers, language and country.</param>
public sealed record ProviderSearchContext(IReadOnlyList<SearchProviderHandle> Providers, string Fingerprint);

/// <summary>
/// Resolves the remote search providers Jellyfin has enabled for an item's library.
/// </summary>
public interface IProviderSearchGateway
{
    /// <summary>Gets the search context for an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="kind">Media kind.</param>
    /// <returns>Context, or null when the item does not exist.</returns>
    ProviderSearchContext? GetSearchContext(Guid itemId, MediaKind kind);
}
