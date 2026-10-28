using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Configuration;
using Jellyfin.Plugin.SmartMatch.Jobs;
using Jellyfin.Plugin.SmartMatch.Library;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Normalization;
using Jellyfin.Plugin.SmartMatch.Services;
using Jellyfin.Plugin.SmartMatch.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace SmartMatch.UnitTests;

/// <summary>Mutable in-memory library item.</summary>
public sealed class FakeItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public MediaKind Kind { get; set; }

    public Guid LibraryId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsInMixedFolder { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLocked { get; set; }

    public bool NameLocked { get; set; }

    public bool HasLocalNfo { get; set; }

    public string? Overview { get; set; }

    public DateTime DateLastSaved { get; set; }
}

public sealed class FakeLibrary : ILibraryGateway
{
    public ConcurrentDictionary<Guid, FakeItem> Items { get; } = new();

    public List<LibraryInfo> Libraries { get; } = new();

    public bool IsLibraryScanRunning { get; set; }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public HashSet<Guid> Busy { get; } = new();

    public int Writes { get; private set; }

    public int Refreshes { get; private set; }

    public List<string> Log { get; } = new();

    /// <summary>Gets or sets an exception thrown by the next refresh.</summary>
    public Exception? FailNextRefresh { get; set; }

    /// <summary>Gets or sets an exception thrown by writes.</summary>
    public Exception? FailWrites { get; set; }

    /// <summary>Gets or sets a hook run inside WriteIdentityAsync (simulate concurrent changes).</summary>
    public Action<FakeItem>? OnWrite { get; set; }

    /// <summary>Gets or sets a function returning the canonical title for a provider id during refresh.</summary>
    public Func<IReadOnlyDictionary<string, string>, string?>? RefreshTitle { get; set; }

    public Guid AddLibrary(string name, string? type = "tvshows", bool sidecar = false)
    {
        var id = Guid.NewGuid();
        Libraries.Add(new LibraryInfo(id, name, type, sidecar, sidecar ? "metadata savers enabled: Nfo" : "none"));
        return id;
    }

    public FakeItem Add(Guid libraryId, MediaKind kind, string path, string? name = null, Dictionary<string, string>? ids = null)
    {
        var basename = path.TrimEnd('/').Split('/').Last();
        var item = new FakeItem
        {
            Kind = kind,
            LibraryId = libraryId,
            Path = path,
            Name = name ?? (kind == MediaKind.Movie && basename.Contains('.') ? basename[..basename.LastIndexOf('.')] : basename),
            ProviderIds = ids ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DateLastSaved = Clock(),
        };
        Items[item.Id] = item;
        return item;
    }

    public IReadOnlyList<LibraryInfo> GetLibraries() => Libraries.ToList();

    public ItemSnapshot? GetItem(Guid itemId)
    {
        if (!Items.TryGetValue(itemId, out var i))
        {
            return null;
        }

        var library = Libraries.FirstOrDefault(l => l.Id == i.LibraryId);
        return new ItemSnapshot
        {
            Id = i.Id,
            Kind = i.Kind,
            LibraryId = i.LibraryId,
            LibraryName = library?.Name ?? string.Empty,
            Name = i.Name,
            Path = i.Path,
            PathIsDirectory = i.Kind == MediaKind.Series,
            IsInMixedFolder = i.IsInMixedFolder,
            ProviderIds = new Dictionary<string, string>(i.ProviderIds, StringComparer.OrdinalIgnoreCase),
            IsLocked = i.IsLocked,
            NameLocked = i.NameLocked,
            HasLocalNfo = i.HasLocalNfo,
        };
    }

    public IReadOnlyList<Guid> GetCandidateItemIds(IReadOnlyCollection<Guid> libraryIds, DateTime? changedSinceUtc)
        => Items.Values.Where(i => libraryIds.Contains(i.LibraryId) && (changedSinceUtc is null || i.DateLastSaved >= changedSinceUtc)).Select(i => i.Id).ToList();

    public string? GetFallbackEpisodeFileName(Guid seriesId) => null;

    public bool IsItemBusy(Guid itemId) => Busy.Contains(itemId);

    public Task WriteIdentityAsync(Guid itemId, IReadOnlyDictionary<string, string> setProviderIds, IReadOnlyCollection<string> removeProviderKeys, string? title, CancellationToken cancellationToken)
    {
        if (FailWrites is not null)
        {
            throw FailWrites;
        }

        var item = Items[itemId];
        OnWrite?.Invoke(item);
        foreach (var k in removeProviderKeys)
        {
            item.ProviderIds.Remove(k);
        }

        foreach (var (k, v) in setProviderIds)
        {
            item.ProviderIds[k] = v;
        }

        if (title is not null)
        {
            item.Name = title;
        }

        item.DateLastSaved = Clock();
        Writes++;
        Log.Add($"write {item.Path} ids={string.Join(",", setProviderIds.Select(kv => kv.Key + "=" + kv.Value))} title={title}");
        return Task.CompletedTask;
    }

    public Task RefreshAsync(Guid itemId, RefreshRequest request, CancellationToken cancellationToken)
    {
        Refreshes++;
        if (FailNextRefresh is { } ex)
        {
            FailNextRefresh = null;
            throw ex;
        }

        var item = Items[itemId];
        item.Overview = "Overview of " + request.CandidateName;
        if (request.ReplaceUnlockedText && !item.NameLocked)
        {
            item.Name = RefreshTitle?.Invoke(request.ProviderIds) ?? request.CandidateName;
        }

        Log.Add($"refresh {item.Path}");
        return Task.CompletedTask;
    }
}

/// <summary>A catalogue-backed provider; titles are matched by comparison key containment.</summary>
public sealed class FakeProviders : IProviderSearchGateway
{
    public List<(string Name, int? Year, MediaKind Kind, Dictionary<string, string> Ids, string Provider)> Catalogue { get; } = new();

    public List<string> ProviderNames { get; } = new() { "TheMovieDb" };

    public List<(string Title, int? Year)> Queries { get; } = new();

    /// <summary>Gets or sets an exception thrown by every search.</summary>
    public Func<Exception>? Fail { get; set; }

    /// <summary>Gets or sets per-provider failures.</summary>
    public Dictionary<string, Func<Exception>> FailProvider { get; } = new();

    public void Add(MediaKind kind, string name, int? year, string tmdb, string provider = "TheMovieDb", string? imdb = null)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = tmdb };
        if (imdb is not null)
        {
            ids["Imdb"] = imdb;
        }

        Catalogue.Add((name, year, kind, ids, provider));
    }

    public ProviderSearchContext? GetSearchContext(Guid itemId, MediaKind kind)
    {
        var handles = ProviderNames.Select(p => new SearchProviderHandle(p, (title, year, ct) =>
        {
            Queries.Add((title, year));
            if (Fail is not null)
            {
                throw Fail();
            }

            if (FailProvider.TryGetValue(p, out var f))
            {
                throw f();
            }

            var key = MediaNameNormalizer.ComparisonKey(title);
            IReadOnlyList<ProviderCandidate> results = Catalogue
                .Where(c => c.Kind == kind && c.Provider == p)
                .Where(c =>
                {
                    var ck = MediaNameNormalizer.ComparisonKey(c.Name);
                    return ck.Contains(key, StringComparison.Ordinal) || key.Contains(ck, StringComparison.Ordinal);
                })
                .Select(c => new ProviderCandidate(c.Name, c.Year, c.Ids, p))
                .ToList();
            return Task.FromResult(results);
        })).ToList();
        return new ProviderSearchContext(handles, string.Join(",", ProviderNames));
    }
}

public sealed class TestConfig : IConfigProvider
{
    public PluginConfiguration Current { get; set; } = new() { MinSearchIntervalMs = 250, StartupDelaySeconds = 0, SettleDelaySeconds = 5 };

    public void Save(PluginConfiguration configuration) => Current = configuration;
}

public sealed class TestServer : IServerInfo
{
    public Version ServerVersion { get; set; } = new(12, 0, 0);

    public bool IsSupported => ServerVersion.Major == 12;
}

public sealed class FakeEventSource : IItemEventSource
{
    public event EventHandler<ItemEvent>? ItemChanged;

    public bool Subscribed { get; private set; }

    public void Raise(Guid id, string name = "ItemAdded", Dictionary<string, string>? ids = null)
        => ItemChanged?.Invoke(this, new ItemEvent(id, name, ids ?? new Dictionary<string, string>()));

    public void Subscribe() => Subscribed = true;

    public void Unsubscribe() => Subscribed = false;
}

/// <summary>Wires every service against the fakes and a temp journal directory.</summary>
public sealed class Harness : IDisposable
{
    public Harness(string? directory = null)
    {
        Directory = directory ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smartmatch-tests", Guid.NewGuid().ToString("N"));
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        Library.Clock = () => Time.GetUtcNow().UtcDateTime;
        Files = new JsonFileStore(Directory, NullLogger.Instance);
        Store = new SmartMatchStore(Files, NullLogger<SmartMatchStore>.Instance);
        Store.Load();
        Executor = new ProviderSearchExecutor(NullLogger<ProviderSearchExecutor>.Instance, Time, (d, ct) => Task.CompletedTask);
        Cache = new ProviderResultCache(Time);
        Analyzer = new ItemAnalyzer(Library, Providers, Executor, Cache, new MediaNameNormalizer(() => 2028), Store, Config, Locks, Time, NullLogger<ItemAnalyzer>.Instance);
        SelfWrites = new SelfWriteTracker(Time);
        Apply = new MatchApplicationService(Library, Store, Config, Locks, SelfWrites, Server, Time, NullLogger<MatchApplicationService>.Instance);
        Bulk = new BulkApplyCoordinator(Store, Apply, Library, Config, Time, NullLogger<BulkApplyCoordinator>.Instance);
        Audits = new AuditService(Store, Library, Config, Server, Time, NullLogger<AuditService>.Instance);
        Observer = new ScanEventObserver(Events, Store, Config, SelfWrites, Status, Time, NullLogger<ScanEventObserver>.Instance);
        Worker = new SmartMatchWorker(Store, Analyzer, Apply, Bulk, Audits, Observer, Library, Config, Server, Status, Time, NullLogger<SmartMatchWorker>.Instance);
    }

    public string Directory { get; }

    public FakeTimeProvider Time { get; }

    public FakeLibrary Library { get; } = new();

    public FakeProviders Providers { get; } = new();

    public TestConfig Config { get; } = new();

    public TestServer Server { get; } = new();

    public FakeEventSource Events { get; } = new();

    public WorkerStatus Status { get; } = new();

    public ItemLockRegistry Locks { get; } = new();

    public JsonFileStore Files { get; }

    public SmartMatchStore Store { get; }

    public ProviderSearchExecutor Executor { get; }

    public ProviderResultCache Cache { get; }

    public ItemAnalyzer Analyzer { get; }

    public SelfWriteTracker SelfWrites { get; }

    public MatchApplicationService Apply { get; }

    public BulkApplyCoordinator Bulk { get; }

    public AuditService Audits { get; }

    public ScanEventObserver Observer { get; }

    public SmartMatchWorker Worker { get; }

    public Task<AnalysisOutcome> Analyze(Guid itemId, FindingOrigin origin = FindingOrigin.ExistingAudit, bool force = false)
        => Analyzer.AnalyzeAsync(new QueueEntry { ItemId = itemId, Origin = origin, Force = force }, CancellationToken.None);

    public Finding FindingFor(Guid itemId) => Store.GetFindingByItem(itemId) ?? throw new InvalidOperationException("no finding");

    /// <summary>Processes every ready queue entry (moves the clock past debounce windows first).</summary>
    public async Task<int> Drain()
    {
        Time.Advance(TimeSpan.FromMinutes(5));
        return await Worker.DrainAsync(CancellationToken.None);
    }

    public void EnableAuto(Guid libraryId)
    {
        Config.Current.Mode = ApplyMode.AutoApplyHighConfidence;
        Config.Current.AutoApplyLibraryIds = new[] { libraryId.ToString() };
    }

    public void Dispose()
    {
        Apply.Dispose();
        Executor.Dispose();
        Files.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
        }
    }

    public static Exception Http(HttpStatusCode code) => new HttpRequestException("status " + (int)code, null, code);
}
