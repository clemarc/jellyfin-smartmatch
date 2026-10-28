using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace SmartMatch.TestProvider;

/// <summary>Test-only plugin entry point.</summary>
public class TestProviderPlugin : BasePlugin<BasePluginConfiguration>
{
    public TestProviderPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "SmartMatch Test Provider";

    public override Guid Id => new("5e1f0c84-7a73-4d6b-9a1c-6d3f0c1b2a99");
}

/// <summary>Synthetic catalogue shared by the series and movie providers.</summary>
internal static class Catalogue
{
    public const string ProviderName = "SmartMatch Test Provider";

    public static readonly (bool Series, string Name, int Year, string Tmdb)[] Entries =
    {
        (true, "The Fall and Rise of Reggie Dinkins", 2026, "990001"),
        (true, "Clean Show", 2020, "990002"),
        (true, "The Office", 2005, "990003"),
        (true, "The Office", 2001, "990004"),
        (true, "Severance", 2022, "990005"),
        (true, "New Auto Show", 2024, "990006"),
        (true, "Later Show", 2023, "990007"),
        (false, "The Dark Knight", 2008, "990101"),
        (false, "Dune", 2021, "990102"),
        (false, "Dune", 1984, "990103"),
        (false, "Arrival", 2016, "990104"),
    };

    public static string Key(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c == '\'')
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static IEnumerable<RemoteSearchResult> Search(bool series, string? name, ILogger logger)
    {
        logger.LogInformation("SmartMatchTestProvider search: {Name}", name);
        if (Key(name).Contains("provider down", StringComparison.Ordinal))
        {
            throw new HttpRequestException("Synthetic outage", null, HttpStatusCode.ServiceUnavailable);
        }

        var key = Key(name);
        return Entries
            .Where(e => e.Series == series && key.Length > 0 && (Key(e.Name).Contains(key, StringComparison.Ordinal) || key.Contains(Key(e.Name), StringComparison.Ordinal)))
            .Select(e => new RemoteSearchResult
            {
                Name = e.Name,
                ProductionYear = e.Year,
                SearchProviderName = ProviderName,
                ProviderIds = new Dictionary<string, string> { ["Tmdb"] = e.Tmdb },
            })
            .ToList();
    }

    public static (bool Series, string Name, int Year, string Tmdb)? Lookup(bool series, ItemLookupInfo info)
    {
        var id = info.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrEmpty(id))
        {
            var byId = Entries.FirstOrDefault(e => e.Series == series && e.Tmdb == id);
            return byId.Name is null ? null : byId;
        }

        // Jellyfin's own matching during a scan: exact name (and year when known), like a real provider
        // that cannot see through "www.UIndex.org - " noise.
        var match = Entries.Where(e => e.Series == series && Key(e.Name) == Key(info.Name) && (info.Year is null || info.Year == e.Year)).ToList();
        return match.Count == 1 ? match[0] : null;
    }
}

public class TestSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>
{
    private readonly ILogger<TestSeriesProvider> _logger;

    public TestSeriesProvider(ILogger<TestSeriesProvider> logger)
    {
        _logger = logger;
    }

    public string Name => Catalogue.ProviderName;

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult(Catalogue.Search(true, searchInfo.Name, _logger));

    public Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var entry = Catalogue.Lookup(true, info);
        var result = new MetadataResult<Series>();
        if (entry is { } e)
        {
            result.HasMetadata = true;
            result.Item = new Series
            {
                Name = e.Name,
                Overview = "Synthetic overview for " + e.Name,
                ProductionYear = e.Year,
                ProviderIds = new Dictionary<string, string> { ["Tmdb"] = e.Tmdb },
            };
        }

        return Task.FromResult(result);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
}

public class TestMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>
{
    private readonly ILogger<TestMovieProvider> _logger;

    public TestMovieProvider(ILogger<TestMovieProvider> logger)
    {
        _logger = logger;
    }

    public string Name => Catalogue.ProviderName;

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult(Catalogue.Search(false, searchInfo.Name, _logger));

    public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var entry = Catalogue.Lookup(false, info);
        var result = new MetadataResult<Movie>();
        if (entry is { } e)
        {
            result.HasMetadata = true;
            result.Item = new Movie
            {
                Name = e.Name,
                Overview = "Synthetic overview for " + e.Name,
                ProductionYear = e.Year,
                ProviderIds = new Dictionary<string, string> { ["Tmdb"] = e.Tmdb },
            };
        }

        return Task.FromResult(result);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
}
