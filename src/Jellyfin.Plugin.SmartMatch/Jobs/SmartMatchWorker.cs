using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Discovery;
using Jellyfin.Plugin.SmartMatch.Library;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Services;
using Jellyfin.Plugin.SmartMatch.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartMatch.Jobs;

/// <summary>
/// The bounded background worker. One item at a time, one provider search at a time, entirely
/// outside Jellyfin's scan and refresh callbacks. It also owns startup recovery, the first-run
/// audit and periodic journal flushing.
/// </summary>
public sealed class SmartMatchWorker : BackgroundService
{
    private readonly SmartMatchStore _store;
    private readonly ItemAnalyzer _analyzer;
    private readonly MatchApplicationService _apply;
    private readonly BulkApplyCoordinator _bulk;
    private readonly AuditService _audits;
    private readonly ScanEventObserver _observer;
    private readonly ILibraryGateway _library;
    private readonly IConfigProvider _config;
    private readonly IServerInfo _server;
    private readonly WorkerStatus _status;
    private readonly TimeProvider _time;
    private readonly ILogger<SmartMatchWorker> _logger;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartMatchWorker"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="analyzer">Analyzer.</param>
    /// <param name="apply">Apply service.</param>
    /// <param name="bulk">Bulk coordinator.</param>
    /// <param name="audits">Audit service.</param>
    /// <param name="observer">Scan observer.</param>
    /// <param name="library">Library gateway.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="server">Server info.</param>
    /// <param name="status">Shared worker status.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public SmartMatchWorker(
        SmartMatchStore store,
        ItemAnalyzer analyzer,
        MatchApplicationService apply,
        BulkApplyCoordinator bulk,
        AuditService audits,
        ScanEventObserver observer,
        ILibraryGateway library,
        IConfigProvider config,
        IServerInfo server,
        WorkerStatus status,
        TimeProvider time,
        ILogger<SmartMatchWorker> logger)
    {
        _store = store;
        _analyzer = analyzer;
        _apply = apply;
        _bulk = bulk;
        _audits = audits;
        _observer = observer;
        _library = library;
        _config = config;
        _server = server;
        _status = status;
        _time = time;
        _logger = logger;
        _observer.WorkQueued += (_, _) => Signal();
        _audits.WorkQueued += (_, _) => Signal();
    }

    /// <summary>Wakes the worker.</summary>
    public void Signal() => _signal.Release();

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _observer.Stop();
        await _bulk.StopAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never blocked by journal loading.
        await Task.Yield();
        try
        {
            _store.Load();
            Recover();
        }
        catch (Exception ex)
        {
            _status.Fault = "Journal could not be loaded: " + ex.Message;
            _logger.LogError(ex, "Smart Match could not load its journal; background work is disabled");
            return;
        }

        if (!_server.IsSupported)
        {
            _status.Fault = $"Jellyfin {_server.ServerVersion} is not a supported ABI; Smart Match is idle.";
            _logger.LogError("Smart Match does not support Jellyfin {Version}; failing closed (no background work, no writes)", _server.ServerVersion);
            await FlushLoopAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        _observer.Start();
        _status.MonitoringActive = true;
        var flush = FlushLoopAsync(stoppingToken);

        try
        {
            var delay = TimeSpan.FromSeconds(Math.Max(0, _config.Current.StartupDelaySeconds));
            _status.StartedUtc = _time.GetUtcNow().UtcDateTime + delay;
            await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            MaybeStartInitialAudit();
            await RunLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        finally
        {
            _status.MonitoringActive = false;
            await flush.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes every queue entry that is ready now, then completes finished runs. Used by tests
    /// and by nothing on Jellyfin's scan path.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of entries processed.</returns>
    internal async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        for (var guard = 0; guard < 10000; guard++)
        {
            var batch = _store.PeekReady(_time.GetUtcNow().UtcDateTime, 20);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var entry in batch)
            {
                await ProcessAsync(entry, cancellationToken).ConfigureAwait(false);
                processed++;
            }
        }

        _audits.CheckCompletion();
        return processed;
    }

    /// <summary>Runs startup recovery (tests).</summary>
    internal void RecoverForTests() => Recover();

    private void Recover()
    {
        // An apply interrupted by a crash: decide from the checkpoint whether anything was written.
        foreach (var f in _store.QueryFindings(f => f.Status == FindingStatus.Applying))
        {
            var decision = f.LastDecisionId is null ? null : _store.GetDecision(f.LastDecisionId.Value);
            var written = decision is not null && decision.WrittenProviderIds.Count > 0;
            _store.TryUpdateFinding(f.ReviewId, null, x =>
            {
                x.Status = written ? FindingStatus.Partial : FindingStatus.Conflict;
                x.ReasonCode = written ? "INTERRUPTED_AFTER_WRITE" : "INTERRUPTED_BEFORE_WRITE";
                x.Reason = written
                    ? "Server stopped after the identity was written; Retry repeats only the refresh."
                    : "Server stopped before anything was written; Retry re-analyses the item.";
                return true;
            }, out _);
        }

        _bulk.RecoverInterrupted();

        try
        {
            // After an update that changes ranking, refresh open findings from saved candidates (no searches).
            _analyzer.RerankStoredFindings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Smart Match could not re-rank saved findings; they keep their previous classification");
        }
    }

    private void MaybeStartInitialAudit()
    {
        var state = _store.GetState();
        if (!_config.Current.InitialAuditEnabled || state.InitialAuditCompleted)
        {
            return;
        }

        if (state.InitialAuditRunId is { } runId && _store.GetAuditRun(runId) is { Status: RunStatus.Running })
        {
            // Resume: the queue is persistent, nothing to re-enqueue.
            _logger.LogInformation("Smart Match resuming initial audit {RunId}", runId);
            return;
        }

        _audits.StartAudit(RunKinds.InitialAudit, "system", false);
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = _config.Current;
            var now = _time.GetUtcNow().UtcDateTime;

            if (_store.GetState().ReconciliationRequired)
            {
                _audits.Reconcile("system");
            }

            if (config.DeferWhileScanning && _library.IsLibraryScanRunning)
            {
                _status.State = "Waiting for library scan to finish";
                await WaitAsync(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                continue;
            }

            var batch = _store.PeekReady(now, 20);
            if (batch.Count == 0)
            {
                _audits.CheckCompletion();
                _status.State = "Idle";
                var next = _store.NextDueUtc();
                var wait = next is null ? TimeSpan.FromMinutes(5) : next.Value - now;
                await WaitAsync(wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : (wait > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : wait), stoppingToken).ConfigureAwait(false);
                continue;
            }

            foreach (var entry in batch)
            {
                stoppingToken.ThrowIfCancellationRequested();
                await ProcessAsync(entry, stoppingToken).ConfigureAwait(false);
                if (config.DeferWhileScanning && _library.IsLibraryScanRunning)
                {
                    break;
                }
            }

            _audits.CheckCompletion();
        }
    }

    private async Task ProcessAsync(QueueEntry entry, CancellationToken stoppingToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_audits.BudgetExhausted(entry.RunId))
        {
            _store.Defer(entry.ItemId, now.AddHours(1));
            return;
        }

        _status.State = "Analysing";
        _status.CurrentItemId = entry.ItemId;
        AnalysisOutcome outcome;
        try
        {
            outcome = await _analyzer.AnalyzeAsync(entry, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let one item stop the worker; never surface into Jellyfin's scan.
            _logger.LogError(ex, "Smart Match failed to analyse item {ItemId}; it will be retried", entry.ItemId);
            _store.Defer(entry.ItemId, now.AddMinutes(10));
            if (entry.Deferrals > 20)
            {
                _store.CompleteQueueEntry(entry.ItemId, DateTime.MaxValue);
            }

            return;
        }
        finally
        {
            _status.CurrentItemId = null;
        }

        _status.Processed++;
        if (outcome.DeferUntilUtc is { } retryAt && (outcome.Searched || outcome.Code == "Deferred:PROVIDER_BACKOFF"))
        {
            // Provider back-off: the attempt counts for the run; the retry continues on its own.
            _audits.RecordOutcome(entry.RunId, outcome with { DeferUntilUtc = null });
            _store.DetachAndDefer(entry.ItemId, retryAt);
            return;
        }

        if (outcome.DeferUntilUtc is { } until)
        {
            if (entry.Deferrals > 200)
            {
                _logger.LogWarning("Smart Match dropped item {ItemId} after repeated deferrals ({Code})", entry.ItemId, outcome.Code);
                _store.CompleteQueueEntry(entry.ItemId, DateTime.MaxValue);
            }
            else
            {
                _store.Defer(entry.ItemId, until);
            }

            return;
        }

        _store.CompleteQueueEntry(entry.ItemId, entry.LastEventUtc);

        _audits.RecordOutcome(entry.RunId, outcome);

        var finding = outcome.Finding;
        var top = finding?.Recommended();
        if (finding is { Status: FindingStatus.Proposed } && top?.AutoEligible == true
            && LibraryScope.AutoApplyEnabled(_config.Current, finding.LibraryId))
        {
            _status.State = "Auto-applying";
            var result = await _apply.ApplyAsync(new ApplyRequest(finding.ReviewId, top.CandidateId, finding.Version, ApplyOrigin.Automatic, "automatic"), stoppingToken).ConfigureAwait(false);
            _audits.RecordAutoApply(entry.RunId, result.Outcome);
            _logger.LogInformation("Smart Match automatic application for item {ItemId}: {Outcome} {Code}", finding.ItemId, result.Outcome, result.Code);
        }
    }

    private async Task WaitAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        try
        {
            await _signal.WaitAsync(timeout, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private async Task FlushLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Smart Match journal flush failed");
            }
        }

        await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// Live worker state for the status endpoint.
/// </summary>
public sealed class WorkerStatus
{
    /// <summary>Gets or sets a value indicating whether event monitoring is subscribed.</summary>
    public bool MonitoringActive { get; set; }

    /// <summary>Gets or sets the human-readable state.</summary>
    public string State { get; set; } = "Starting";

    /// <summary>Gets or sets the item being analysed.</summary>
    public Guid? CurrentItemId { get; set; }

    /// <summary>Gets or sets when background processing starts (after the startup delay).</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>Gets or sets the number of items processed since start.</summary>
    public long Processed { get; set; }

    /// <summary>Gets or sets a fatal fault description.</summary>
    public string? Fault { get; set; }

    /// <summary>Gets or sets the number of scan events received.</summary>
    public long EventsReceived { get; set; }

    /// <summary>Gets or sets the number of events ignored as self-generated.</summary>
    public long EventsSuppressed { get; set; }

    /// <summary>Gets or sets the time of the last scan event.</summary>
    public DateTime? LastEventUtc { get; set; }
}
