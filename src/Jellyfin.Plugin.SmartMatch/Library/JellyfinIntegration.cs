using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Jobs;
using Jellyfin.Plugin.SmartMatch.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.SmartMatch.Library;

/// <summary>
/// Supported ABI check: the plugin is built against Jellyfin 12 and fails closed on anything else.
/// </summary>
public sealed class JellyfinServerInfo : IServerInfo
{
    /// <summary>Supported Jellyfin major version.</summary>
    public const int SupportedMajor = 12;

    private readonly IServerApplicationHost _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinServerInfo"/> class.
    /// </summary>
    /// <param name="host">Server host.</param>
    public JellyfinServerInfo(IServerApplicationHost host)
    {
        _host = host;
    }

    /// <inheritdoc />
    public Version ServerVersion => _host.ApplicationVersion;

    /// <inheritdoc />
    public bool IsSupported => _host.ApplicationVersion.Major == SupportedMajor;
}

/// <summary>
/// Copies Jellyfin item events into <see cref="ItemEvent"/> signals. Handlers do no I/O.
/// </summary>
public sealed class JellyfinItemEventSource : IItemEventSource
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinItemEventSource"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Provider manager.</param>
    public JellyfinItemEventSource(ILibraryManager libraryManager, IProviderManager providerManager)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
    }

    /// <inheritdoc />
    public event EventHandler<ItemEvent>? ItemChanged;

    /// <inheritdoc />
    public void Subscribe()
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _providerManager.RefreshCompleted += OnRefreshCompleted;
    }

    /// <inheritdoc />
    public void Unsubscribe()
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _providerManager.RefreshCompleted -= OnRefreshCompleted;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => Raise(e.Item, "ItemAdded");

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e) => Raise(e.Item, "ItemUpdated");

    private void OnRefreshCompleted(object? sender, Jellyfin.Data.Events.GenericEventArgs<BaseItem> e) => Raise(e.Argument, "RefreshCompleted");

    private void Raise(BaseItem? item, string name)
    {
        if (item is not (Series or Movie) || item.IsVirtualItem)
        {
            return;
        }

        ItemChanged?.Invoke(this, new ItemEvent(item.Id, name, new Dictionary<string, string>(item.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// Post-scan reconciliation hook. Runs inside Jellyfin's scan finalisation, so it only enqueues
/// ids (bounded, no remote calls); the worker does the searching later.
/// </summary>
public sealed class SmartMatchPostScanTask : ILibraryPostScanTask
{
    private readonly AuditService _audits;
    private readonly IServerInfo _server;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartMatchPostScanTask"/> class.
    /// </summary>
    /// <param name="audits">Audit service.</param>
    /// <param name="server">Server info.</param>
    public SmartMatchPostScanTask(AuditService audits, IServerInfo server)
    {
        _audits = audits;
        _server = server;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (_server.IsSupported && Plugin.Instance?.Configuration.MonitorNewScans != false)
        {
            _audits.Reconcile("post-scan");
        }

        progress.Report(100);
        return Task.CompletedTask;
    }
}

/// <summary>
/// "Smart Match: audit existing library" scheduled task (manual or admin-scheduled full audit).
/// </summary>
public sealed class AuditLibraryTask : IScheduledTask
{
    private readonly AuditService _audits;
    private readonly IServerInfo _server;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditLibraryTask"/> class.
    /// </summary>
    /// <param name="audits">Audit service.</param>
    /// <param name="server">Server info.</param>
    public AuditLibraryTask(AuditService audits, IServerInfo server)
    {
        _audits = audits;
        _server = server;
    }

    /// <inheritdoc />
    public string Name => "Smart Match: audit existing library";

    /// <inheritdoc />
    public string Key => "SmartMatchAudit";

    /// <inheritdoc />
    public string Description => "Checks every film and series in the selected libraries and updates the Smart Match findings report. Report-only unless auto-apply is enabled.";

    /// <inheritdoc />
    public string Category => "Smart Match";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_server.IsSupported)
        {
            return;
        }

        var run = _audits.StartAudit(RunKinds.Audit, "scheduled-task", false);
        await _audits.WaitForRunAsync(run.RunId, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}

/// <summary>
/// "Smart Match: reconcile library" scheduled task (fallback in case scan events or the post-scan hook were missed).
/// </summary>
public sealed class ReconcileLibraryTask : IScheduledTask
{
    private readonly AuditService _audits;
    private readonly IServerInfo _server;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconcileLibraryTask"/> class.
    /// </summary>
    /// <param name="audits">Audit service.</param>
    /// <param name="server">Server info.</param>
    public ReconcileLibraryTask(AuditService audits, IServerInfo server)
    {
        _audits = audits;
        _server = server;
    }

    /// <inheritdoc />
    public string Name => "Smart Match: reconcile library";

    /// <inheritdoc />
    public string Description => "Queues films and series that are new, changed or due for a provider retry. Cheap; searches happen in the background.";

    /// <inheritdoc />
    public string Key => "SmartMatchReconcile";

    /// <inheritdoc />
    public string Category => "Smart Match";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_server.IsSupported)
        {
            return;
        }

        var run = _audits.Reconcile("scheduled-task");
        await _audits.WaitForRunAsync(run.RunId, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks },
    };
}
