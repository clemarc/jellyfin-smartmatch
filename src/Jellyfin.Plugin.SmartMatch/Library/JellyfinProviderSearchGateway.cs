using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Normalization;
using Jellyfin.Plugin.SmartMatch.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.SmartMatch.Library;

/// <summary>
/// Resolves the remote search providers Jellyfin has enabled for an item's library and media type.
/// Uses <see cref="IProviderManager.GetMetadataProviders{T}(BaseItem, MediaBrowser.Model.Configuration.LibraryOptions)"/>
/// (disabled providers excluded) and calls each <see cref="IRemoteSearchProvider{TLookupInfoType}"/> directly so
/// failures are visible; only the clean title, trusted year, language and country are sent, never a path.
/// </summary>
public sealed class JellyfinProviderSearchGateway : IProviderSearchGateway
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinProviderSearchGateway"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Provider manager.</param>
    public JellyfinProviderSearchGateway(ILibraryManager libraryManager, IProviderManager providerManager)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
    }

    /// <inheritdoc />
    public ProviderSearchContext? GetSearchContext(Guid itemId, MediaKind kind)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var libraryOptions = _libraryManager.GetLibraryOptions(item);
        var language = item.GetPreferredMetadataLanguage();
        var country = item.GetPreferredMetadataCountryCode();

        List<SearchProviderHandle> handles;
        if (item is Series)
        {
            handles = _providerManager.GetMetadataProviders<Series>(item, libraryOptions)
                .OfType<IRemoteSearchProvider<SeriesInfo>>()
                .Select(p => Handle(p, () => new SeriesInfo(), language, country))
                .ToList();
        }
        else if (item is Movie)
        {
            handles = _providerManager.GetMetadataProviders<Movie>(item, libraryOptions)
                .OfType<IRemoteSearchProvider<MovieInfo>>()
                .Select(p => Handle(p, () => new MovieInfo(), language, country))
                .ToList();
        }
        else
        {
            return null;
        }

        var fingerprint = Fingerprints.Hash(kind + "|" + string.Join(",", handles.Select(h => h.Name)) + "|" + language + "|" + country, 16);
        return new ProviderSearchContext(handles, fingerprint);
    }

    private static SearchProviderHandle Handle<TLookup>(IRemoteSearchProvider<TLookup> provider, Func<TLookup> create, string language, string country)
        where TLookup : ItemLookupInfo
    {
        return new SearchProviderHandle(provider.Name, async (title, year, ct) =>
        {
            var info = create();
            info.Name = title;
            info.Year = year;
            info.MetadataLanguage = language;
            info.MetadataCountryCode = country;
            info.IsAutomated = true;
            var results = await provider.GetSearchResults(info, ct).ConfigureAwait(false);
            return results
                .Where(r => r is not null)
                .Select(r => new ProviderCandidate(
                    r.Name ?? string.Empty,
                    r.ProductionYear,
                    new Dictionary<string, string>(r.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                    provider.Name))
                .ToList();
        });
    }
}
