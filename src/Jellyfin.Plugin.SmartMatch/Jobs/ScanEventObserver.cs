using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Services;
using Jellyfin.Plugin.SmartMatch.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartMatch.Jobs;

/// <summary>
/// A Series/Movie change signal copied out of a Jellyfin event.
/// </summary>
/// <param name="ItemId">Item id.</param>
/// <param name="EventName">ItemAdded, ItemUpdated or RefreshCompleted.</param>
/// <param name="ProviderIds">Provider ids at event time.</param>
public sealed record ItemEvent(Guid ItemId, string EventName, IReadOnlyDictionary<string, string> ProviderIds);

/// <summary>
/// Source of library change signals (Jellyfin events in production, fakes in tests).
/// </summary>
public interface IItemEventSource
{
    /// <summary>Raised for Series/Movie, non-virtual items only.</summary>
    event EventHandler<ItemEvent>? ItemChanged;

    /// <summary>Subscribes to Jellyfin events.</summary>
    void Subscribe();

    /// <summary>Unsubscribes from Jellyfin events.</summary>
    void Unsubscribe();
}

/// <summary>
/// Lightweight event capture. Handlers only copy the item id into the persistent, coalescing
/// queue and return: no network calls, no metadata writes, no waiting inside Jellyfin's scan
/// or refresh callbacks. Events caused by the plugin's own writes are suppressed.
/// </summary>
public sealed class ScanEventObserver
{
    private readonly IItemEventSource _source;
    private readonly SmartMatchStore _store;
    private readonly IConfigProvider _config;
    private readonly SelfWriteTracker _selfWrites;
    private readonly WorkerStatus _status;
    private readonly TimeProvider _time;
    private readonly ILogger<ScanEventObserver> _logger;
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanEventObserver"/> class.
    /// </summary>
    /// <param name="source">Event source.</param>
    /// <param name="store">Store.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="selfWrites">Self-write tracker.</param>
    /// <param name="status">Worker status.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public ScanEventObserver(IItemEventSource source, SmartMatchStore store, IConfigProvider config, SelfWriteTracker selfWrites, WorkerStatus status, TimeProvider time, ILogger<ScanEventObserver> logger)
    {
        _source = source;
        _store = store;
        _config = config;
        _selfWrites = selfWrites;
        _status = status;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised after an item was queued.</summary>
    public event EventHandler? WorkQueued;

    /// <summary>Subscribes (idempotent).</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _source.ItemChanged += OnItemChanged;
        _source.Subscribe();
        _started = true;
    }

    /// <summary>Unsubscribes (idempotent).</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _source.Unsubscribe();
        _source.ItemChanged -= OnItemChanged;
        _started = false;
    }

    /// <summary>
    /// Handles one event. Public for tests.
    /// </summary>
    /// <param name="sender">Sender.</param>
    /// <param name="e">Event.</param>
    public void OnItemChanged(object? sender, ItemEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        try
        {
            var config = _config.Current;
            if (!config.MonitorNewScans || !_store.IsLoaded)
            {
                return;
            }

            _status.EventsReceived++;
            _status.LastEventUtc = _time.GetUtcNow().UtcDateTime;
            if (_selfWrites.IsSuppressed(e.ItemId))
            {
                _status.EventsSuppressed++;
                return;
            }

            // Identified items with no open finding need no work; skip queue churn.
            var validator = new ProviderIdValidator(config.ExtraProviderIdKeys);
            if (validator.HasReliableIdentity(e.ProviderIds) && !_store.HasFinding(e.ItemId))
            {
                return;
            }

            var notBefore = _time.GetUtcNow().UtcDateTime.AddSeconds(config.SettleDelaySeconds);
            if (_store.Enqueue(e.ItemId, FindingOrigin.NewScan, null, notBefore, false))
            {
                WorkQueued?.Invoke(this, EventArgs.Empty);
            }
        }
#pragma warning disable CA1031 // An observer must never throw into Jellyfin's scanner.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Smart Match ignored an item event it could not record");
        }
    }
}
