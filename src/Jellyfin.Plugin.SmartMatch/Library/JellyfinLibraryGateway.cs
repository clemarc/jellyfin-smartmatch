using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartMatch.Normalization;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartMatch.Library;

/// <summary>
/// Jellyfin 12 implementation of <see cref="ILibraryGateway"/>. Uses only public library and
/// provider-manager APIs: no SQL, no direct database writes, no media file operations.
/// </summary>
public sealed class JellyfinLibraryGateway : ILibraryGateway
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<JellyfinLibraryGateway> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLibraryGateway"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system abstraction (metadata reads only).</param>
    /// <param name="logger">Logger.</param>
    public JellyfinLibraryGateway(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILogger<JellyfinLibraryGateway> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsLibraryScanRunning => _libraryManager.IsScanRunning;

    /// <inheritdoc />
    public IReadOnlyList<LibraryInfo> GetLibraries()
    {
        var result = new List<LibraryInfo>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var id))
            {
                continue;
            }

            if (folder.CollectionType is not (null or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows))
            {
                continue;
            }

            var options = folder.LibraryOptions;
            var savers = options?.MetadataSavers ?? Array.Empty<string>();
            var saveLocal = options?.SaveLocalMetadata ?? false;
            var risk = savers.Length > 0 || saveLocal;
            var detail = risk
                ? string.Join("; ", new[]
                {
                    savers.Length > 0 ? "metadata savers enabled: " + string.Join(", ", savers) : null,
                    saveLocal ? "\"Save artwork into media folders\" is on" : null,
                }.Where(s => s is not null))
                : "No metadata savers and no artwork saving into media folders.";
            result.Add(new LibraryInfo(id, folder.Name, folder.CollectionType?.ToString(), risk, detail));
        }

        return result;
    }

    /// <inheritdoc />
    public ItemSnapshot? GetItem(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is not (Series or Movie))
        {
            return null;
        }

        var library = _libraryManager.GetCollectionFolders(item).FirstOrDefault();
        var path = item.Path;
        var isDirectory = item is Series || (item is Video v && v.VideoType is VideoType.BluRay or VideoType.Dvd);
        return new ItemSnapshot
        {
            Id = item.Id,
            Kind = item is Series ? MediaKind.Series : MediaKind.Movie,
            LibraryId = library?.Id ?? Guid.Empty,
            LibraryName = library?.Name ?? string.Empty,
            Name = item.Name ?? string.Empty,
            Path = path,
            PathIsDirectory = isDirectory,
            IsInMixedFolder = item.IsInMixedFolder,
            ProductionYear = item.ProductionYear,
            ProviderIds = new Dictionary<string, string>(item.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            IsLocked = item.IsLocked,
            NameLocked = item.LockedFields?.Contains(MetadataField.Name) ?? false,
            IsVirtual = item.IsVirtualItem,
            HasLocalNfo = HasLocalNfo(item),
            DateLastSaved = item.DateLastSaved,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetCandidateItemIds(IReadOnlyCollection<Guid> libraryIds, DateTime? changedSinceUtc)
    {
        var ids = new List<Guid>();
        foreach (var libraryId in libraryIds)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie },
                AncestorIds = new[] { libraryId },
                Recursive = true,
                IsVirtualItem = false,
                MinDateLastSaved = changedSinceUtc,
                DtoOptions = new DtoOptions(false) { EnableImages = false },
            };
            ids.AddRange(_libraryManager.GetItemIds(query));
        }

        return ids.Distinct().ToList();
    }

    /// <inheritdoc />
    public string? GetFallbackEpisodeFileName(Guid seriesId)
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { seriesId },
            Recursive = true,
            IsVirtualItem = false,
            Limit = 1,
            DtoOptions = new DtoOptions(false) { EnableImages = false },
        });
        var episode = episodes.Count > 0 ? episodes[0] : null;
        return episode?.Path is { } p ? Path.GetFileName(p) : null;
    }

    /// <inheritdoc />
    public bool IsItemBusy(Guid itemId)
        => _providerManager.GetRefreshProgress(itemId) is not null || _providerManager.GetRefreshQueue().Contains(itemId);

    /// <inheritdoc />
    public async Task WriteIdentityAsync(Guid itemId, IReadOnlyDictionary<string, string> setProviderIds, IReadOnlyCollection<string> removeProviderKeys, string? title, CancellationToken cancellationToken)
    {
        // Re-read the live item: never persist a stale in-memory copy from the audit.
        var item = _libraryManager.GetItemById(itemId) ?? throw new InvalidOperationException("Item no longer exists.");
        item.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in removeProviderKeys)
        {
            item.ProviderIds.Remove(key);
        }

        foreach (var (key, value) in setProviderIds)
        {
            if (!item.TrySetProviderId(key, value))
            {
                throw new InvalidOperationException($"Jellyfin rejected provider id {key}.");
            }
        }

        if (title is not null)
        {
            item.Name = title;
        }

        // MetadataDownload (< MetadataEdit): Jellyfin only runs metadata savers if the library
        // has saving enabled; it does not rewrite pre-existing NFO files for a "manual edit".
        var parent = item.GetParent();
        await _libraryManager.UpdateItemAsync(item, parent, ItemUpdateType.MetadataDownload, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RefreshAsync(Guid itemId, RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = _libraryManager.GetItemById(itemId) ?? throw new InvalidOperationException("Item no longer exists.");
        var searchResult = new RemoteSearchResult
        {
            Name = request.CandidateName,
            ProductionYear = request.CandidateYear,
            ProviderIds = new Dictionary<string, string>(request.ProviderIds, StringComparer.OrdinalIgnoreCase),
            SearchProviderName = request.ProviderName,
        };

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = request.ReplaceUnlockedText,
            ReplaceAllImages = false,
            RemoveOldMetadata = false,
            SearchResult = searchResult,
            IsAutomated = false,
        };

        await _providerManager.RefreshFullItem(item, options, cancellationToken).ConfigureAwait(false);
    }

    private bool HasLocalNfo(BaseItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(item.Path) || !item.IsFileProtocol)
            {
                return false;
            }

            if (item is Series)
            {
                return File.Exists(Path.Combine(item.Path, "tvshow.nfo"));
            }

            var directory = Path.GetDirectoryName(item.Path);
            if (directory is null)
            {
                return false;
            }

            return File.Exists(Path.ChangeExtension(item.Path, ".nfo"))
                || (!item.IsInMixedFolder && File.Exists(Path.Combine(directory, "movie.nfo")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not check for a local NFO for item {ItemId}", item.Id);
            return false;
        }
    }
}
