using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Discovery;
using Jellyfin.Plugin.SmartMatch.Library;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartMatch.Services;

/// <summary>
/// Run kinds.
/// </summary>
public static class RunKinds
{
    /// <summary>First-install audit of existing media.</summary>
    public const string InitialAudit = "InitialAudit";

    /// <summary>Manual or scheduled full audit.</summary>
    public const string Audit = "Audit";

    /// <summary>Post-scan or scheduled reconciliation.</summary>
    public const string Reconciliation = "Reconciliation";
}

/// <summary>
/// Starts, tracks and cancels audits of already-indexed media and reconciliations.
/// Enumeration is id-only (cheap); all remote work happens later in the bounded worker,
/// so a post-scan callback returns quickly. The persistent queue makes runs resumable.
/// </summary>
public sealed class AuditService
{
    private readonly SmartMatchStore _store;
    private readonly ILibraryGateway _library;
    private readonly IConfigProvider _config;
    private readonly IServerInfo _server;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditService> _logger;
    private readonly object _sync = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditService"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="library">Library gateway.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="server">Server info.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public AuditService(SmartMatchStore store, ILibraryGateway library, IConfigProvider config, IServerInfo server, TimeProvider time, ILogger<AuditService> logger)
    {
        _store = store;
        _library = library;
        _config = config;
        _server = server;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised when work is queued so the worker wakes up.</summary>
    public event EventHandler? WorkQueued;

    /// <summary>
    /// Starts a full audit, or returns the audit that is already running (single-run lock).
    /// </summary>
    /// <param name="kind">InitialAudit or Audit.</param>
    /// <param name="actor">Who started it.</param>
    /// <param name="force">Search unchanged items again.</param>
    /// <returns>The run.</returns>
    public AuditRun StartAudit(string kind, string actor, bool force)
    {
        lock (_sync)
        {
            var running = _store.GetAuditRuns().FirstOrDefault(r => r.Status == RunStatus.Running && r.Kind != RunKinds.Reconciliation);
            if (running is not null)
            {
                return running;
            }

            var config = _config.Current;
            var libraries = LibraryScope.InScope(_library.GetLibraries(), config);
            var ids = _library.GetCandidateItemIds(libraries, null);
            var run = NewRun(kind, actor, force);
            run.Enqueued = ids.Count;
            _store.SaveAuditRun(run);
            var now = _time.GetUtcNow().UtcDateTime;

            // A full audit covers everything a reconciliation would, so it is the new change baseline.
            _store.UpdateState(s =>
            {
                s.LastReconciliationUtc = now;
                s.ReconciliationRequired = false;
                if (kind == RunKinds.InitialAudit)
                {
                    s.InitialAuditRunId = run.RunId;
                }
            });

            foreach (var id in ids)
            {
                _store.Enqueue(id, FindingOrigin.ExistingAudit, run.RunId, now, force);
            }

            _store.PruneMissing(ids.ToHashSet(), libraries);
            _logger.LogInformation("Smart Match {Kind} {RunId} queued {Count} item(s)", kind, run.RunId, ids.Count);
            CheckCompletion();
            WorkQueued?.Invoke(this, EventArgs.Empty);
            return _store.GetAuditRun(run.RunId)!;
        }
    }

    /// <summary>
    /// Queues items that are unchecked, changed since the last reconciliation, or due for a provider retry.
    /// Only short, bounded work: id queries and enqueue.
    /// </summary>
    /// <param name="actor">Who started it.</param>
    /// <returns>The run.</returns>
    public AuditRun Reconcile(string actor)
    {
        lock (_sync)
        {
            var config = _config.Current;
            var now = _time.GetUtcNow().UtcDateTime;
            var state = _store.GetState();
            var libraries = LibraryScope.InScope(_library.GetLibraries(), config);
            var all = _library.GetCandidateItemIds(libraries, null);
            var covered = _store.CoveredItemIds();
            var changed = state.LastReconciliationUtc is { } since && !state.ReconciliationRequired
                ? _library.GetCandidateItemIds(libraries, since.AddMinutes(-5)).ToHashSet()
                : all.ToHashSet();
            var retry = _store.QueryFindings(f => f.Status == FindingStatus.ProviderError && f.NextRetryUtc is { } r && r <= now).Select(f => f.ItemId);
            var queue = all.Where(id => !covered.Contains(id) || changed.Contains(id)).Concat(retry).Distinct().ToList();

            var run = NewRun(RunKinds.Reconciliation, actor, false);
            run.Enqueued = queue.Count;
            _store.SaveAuditRun(run);
            foreach (var id in queue)
            {
                _store.Enqueue(id, FindingOrigin.Reconciliation, run.RunId, now, false);
            }

            var pruned = _store.PruneMissing(all.ToHashSet(), libraries);
            _store.UpdateState(s =>
            {
                s.LastReconciliationUtc = now;
                s.ReconciliationRequired = false;
            });
            _logger.LogInformation("Smart Match reconciliation {RunId} queued {Count} item(s), pruned {Pruned} removed item(s)", run.RunId, queue.Count, pruned);
            CheckCompletion();
            WorkQueued?.Invoke(this, EventArgs.Empty);
            return _store.GetAuditRun(run.RunId)!;
        }
    }

    /// <summary>
    /// Cancels a run: removes its queued work; completed results stay.
    /// </summary>
    /// <param name="runId">Run.</param>
    /// <returns>Updated run or null.</returns>
    public AuditRun? Cancel(Guid runId)
    {
        lock (_sync)
        {
            var run = _store.GetAuditRun(runId);
            if (run is null || run.Status != RunStatus.Running)
            {
                return run;
            }

            var removed = _store.RemoveRunEntries(runId);
            _logger.LogInformation("Smart Match run {RunId} cancelled; {Removed} queued item(s) dropped", runId, removed);
            return _store.UpdateAuditRun(runId, r =>
            {
                r.Status = RunStatus.Cancelled;
                r.EndedAtUtc = _time.GetUtcNow().UtcDateTime;
            });
        }
    }

    /// <summary>
    /// Records an analysis outcome against its run.
    /// </summary>
    /// <param name="runId">Run.</param>
    /// <param name="outcome">Outcome.</param>
    public void RecordOutcome(Guid? runId, AnalysisOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (runId is null || outcome.DeferUntilUtc is not null)
        {
            return;
        }

        _store.UpdateAuditRun(runId.Value, r =>
        {
            var key = outcome.Code.StartsWith("Skipped:", StringComparison.Ordinal) ? "Skipped" : outcome.Code;
            r.Counts[key] = r.Counts.GetValueOrDefault(key) + 1;
            r.Counts["Processed"] = r.Counts.GetValueOrDefault("Processed") + 1;
            if (outcome.Code.StartsWith("Skipped:", StringComparison.Ordinal))
            {
                var reason = outcome.Code["Skipped:".Length..];
                r.SkipReasons[reason] = r.SkipReasons.GetValueOrDefault(reason) + 1;
            }

            if (outcome.Searched)
            {
                r.Searches++;
            }

            if (outcome.Finding?.ProviderErrors is { Count: > 0 } errors && r.ProviderErrors.Count < 50)
            {
                r.ProviderErrors.AddRange(errors.Take(50 - r.ProviderErrors.Count));
            }
        });
    }

    /// <summary>
    /// Records an automatic application outcome.
    /// </summary>
    /// <param name="runId">Run.</param>
    /// <param name="outcome">Outcome.</param>
    public void RecordAutoApply(Guid? runId, ApplyOutcome outcome)
    {
        if (runId is null)
        {
            return;
        }

        _store.UpdateAuditRun(runId.Value, r =>
        {
            var key = "Auto" + outcome;
            r.Counts[key] = r.Counts.GetValueOrDefault(key) + 1;
        });
    }

    /// <summary>Returns true when the run exhausted its search budget.</summary>
    /// <param name="runId">Run.</param>
    /// <returns>True when exhausted.</returns>
    public bool BudgetExhausted(Guid? runId)
    {
        if (runId is null)
        {
            return false;
        }

        var run = _store.GetAuditRun(runId.Value);
        return run is not null && run.Searches >= _config.Current.MaxSearchesPerRun;
    }

    /// <summary>
    /// Completes runs with no remaining queued work.
    /// </summary>
    public void CheckCompletion()
    {
        foreach (var run in _store.GetAuditRuns().Where(r => r.Status == RunStatus.Running))
        {
            if (_store.QueueCountForRun(run.RunId) > 0)
            {
                continue;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            _store.UpdateAuditRun(run.RunId, r =>
            {
                r.Status = RunStatus.Completed;
                r.EndedAtUtc = now;
            });
            if (run.Kind != RunKinds.Reconciliation)
            {
                _store.UpdateState(s =>
                {
                    s.LastAuditCompletedUtc = now;
                    if (run.Kind == RunKinds.InitialAudit || s.InitialAuditRunId == run.RunId)
                    {
                        s.InitialAuditCompleted = true;
                    }
                });
            }

            _logger.LogInformation("Smart Match run {RunId} ({Kind}) completed", run.RunId, run.Kind);
        }
    }

    /// <summary>
    /// Waits for a run to finish, reporting progress (scheduled task UI).
    /// </summary>
    /// <param name="runId">Run.</param>
    /// <param name="progress">Progress sink (0-100).</param>
    /// <param name="cancellationToken">Cancellation token; cancelling cancels the run.</param>
    /// <returns>Task.</returns>
    public async Task WaitForRunAsync(Guid runId, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var run = _store.GetAuditRun(runId);
                if (run is null || run.Status != RunStatus.Running)
                {
                    progress?.Report(100);
                    return;
                }

                var remaining = _store.QueueCountForRun(runId);
                if (run.Enqueued > 0)
                {
                    progress?.Report(Math.Clamp(100.0 * (run.Enqueued - remaining) / run.Enqueued, 0, 99.9));
                }

                await Task.Delay(TimeSpan.FromSeconds(2), _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Cancel(runId);
            throw;
        }
    }

    private AuditRun NewRun(string kind, string actor, bool force) => new()
    {
        RunId = Guid.NewGuid(),
        Kind = kind,
        StartedBy = actor,
        StartedAtUtc = _time.GetUtcNow().UtcDateTime,
        Status = RunStatus.Running,
        Mode = _config.Current.Mode.ToString(),
        JellyfinVersion = _server.ServerVersion.ToString(),
        PluginVersion = typeof(AuditService).Assembly.GetName().Version?.ToString() ?? "unknown",
        Force = force,
    };
}
