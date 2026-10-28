using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartMatch.Storage;

/// <summary>
/// Durable plugin state that is not user configuration.
/// </summary>
public sealed class StoreState
{
    /// <summary>Gets or sets the salt for path hashes (random per installation).</summary>
    public string PathSalt { get; set; } = string.Empty;

    /// <summary>Gets or sets the install time.</summary>
    public DateTime InstalledUtc { get; set; }

    /// <summary>Gets or sets the initial audit run id once started.</summary>
    public Guid? InitialAuditRunId { get; set; }

    /// <summary>Gets or sets a value indicating whether the initial audit completed.</summary>
    public bool InitialAuditCompleted { get; set; }

    /// <summary>Gets or sets the last reconciliation time.</summary>
    public DateTime? LastReconciliationUtc { get; set; }

    /// <summary>Gets or sets the last full audit completion time.</summary>
    public DateTime? LastAuditCompletedUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the queue overflowed and a reconciliation is owed.</summary>
    public bool ReconciliationRequired { get; set; }
}

/// <summary>
/// Single-writer, in-memory journal with debounced crash-safe persistence.
/// Every public method takes the lock and returns copies, so API readers never observe a half-updated record.
/// </summary>
public sealed class SmartMatchStore
{
    /// <summary>Maximum queued items; overflow schedules a reconciliation instead of growing memory.</summary>
    public const int MaxQueue = 50000;

    /// <summary>Maximum stored findings.</summary>
    public const int MaxFindings = 20000;

    private const string StateFile = "state.json";
    private const string FindingsFile = "findings.json";
    private const string DecisionsFile = "decisions.json";
    private const string CoverageFile = "coverage.json";
    private const string QueueFile = "queue.json";
    private const string RunsFile = "runs.json";

    private readonly object _sync = new();
    private readonly JsonFileStore _files;
    private readonly ILogger _logger;
    private readonly List<string> _loadWarnings = new();
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    private StoreState _state = new();
    private Dictionary<Guid, Finding> _findings = new();
    private Dictionary<Guid, Guid> _findingByItem = new();
    private List<DecisionRecord> _decisions = new();
    private Dictionary<Guid, CoverageEntry> _coverage = new();
    private Dictionary<Guid, QueueEntry> _queue = new();
    private RunsDocument _runs = new();
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartMatchStore"/> class.
    /// </summary>
    /// <param name="files">File store.</param>
    /// <param name="logger">Logger.</param>
    public SmartMatchStore(JsonFileStore files, ILogger<SmartMatchStore> logger)
    {
        _files = files;
        _logger = logger;
    }

    /// <summary>Gets load warnings (corrupt files recovered) for the health report.</summary>
    public IReadOnlyList<string> LoadWarnings
    {
        get
        {
            lock (_sync)
            {
                return _loadWarnings.ToList();
            }
        }
    }

    /// <summary>Gets a value indicating whether the store has been loaded.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_sync)
            {
                return _loaded;
            }
        }
    }

    /// <summary>Gets the data directory.</summary>
    public string DirectoryPath => _files.DirectoryPath;

    /// <summary>
    /// Loads all journal files. Safe to call more than once.
    /// </summary>
    public void Load()
    {
        lock (_sync)
        {
            if (_loaded)
            {
                return;
            }

            _state = LoadFile<StoreState>(StateFile) ?? new StoreState();
            if (string.IsNullOrEmpty(_state.PathSalt))
            {
                _state.PathSalt = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                _state.InstalledUtc = DateTime.UtcNow;
                _dirty.Add(StateFile);
            }

            var findings = LoadFile<List<Finding>>(FindingsFile) ?? new List<Finding>();
            _findings = new Dictionary<Guid, Finding>();
            _findingByItem = new Dictionary<Guid, Guid>();
            foreach (var f in findings.Where(f => f.ReviewId != Guid.Empty && f.ItemId != Guid.Empty))
            {
                // Keep one finding per item (the newest) even if an old journal contained duplicates.
                if (_findingByItem.TryGetValue(f.ItemId, out var existingId) && _findings[existingId].UpdatedUtc >= f.UpdatedUtc)
                {
                    continue;
                }

                if (_findingByItem.TryGetValue(f.ItemId, out existingId))
                {
                    _findings.Remove(existingId);
                }

                _findings[f.ReviewId] = f;
                _findingByItem[f.ItemId] = f.ReviewId;
            }

            _decisions = LoadFile<List<DecisionRecord>>(DecisionsFile) ?? new List<DecisionRecord>();
            _coverage = LoadFile<Dictionary<Guid, CoverageEntry>>(CoverageFile) ?? new Dictionary<Guid, CoverageEntry>();
            _queue = LoadFile<Dictionary<Guid, QueueEntry>>(QueueFile) ?? new Dictionary<Guid, QueueEntry>();
            _runs = LoadFile<RunsDocument>(RunsFile) ?? new RunsDocument();
            _loaded = true;
        }
    }

    // ---------------------------------------------------------------- state

    /// <summary>Gets a copy of the state.</summary>
    /// <returns>State copy.</returns>
    public StoreState GetState()
    {
        lock (_sync)
        {
            return Clone(_state);
        }
    }

    /// <summary>Mutates the state.</summary>
    /// <param name="mutate">Mutation.</param>
    public void UpdateState(Action<StoreState> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_sync)
        {
            mutate(_state);
            _dirty.Add(StateFile);
        }
    }

    // ------------------------------------------------------------- findings

    /// <summary>Gets a finding by review id.</summary>
    /// <param name="reviewId">Review id.</param>
    /// <returns>Copy or null.</returns>
    public Finding? GetFinding(Guid reviewId)
    {
        lock (_sync)
        {
            return _findings.TryGetValue(reviewId, out var f) ? Clone(f) : null;
        }
    }

    /// <summary>Gets the finding for an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Copy or null.</returns>
    public Finding? GetFindingByItem(Guid itemId)
    {
        lock (_sync)
        {
            return _findingByItem.TryGetValue(itemId, out var id) && _findings.TryGetValue(id, out var f) ? Clone(f) : null;
        }
    }

    /// <summary>Returns true when a finding exists for the item (no copy).</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>True when present.</returns>
    public bool HasFinding(Guid itemId)
    {
        lock (_sync)
        {
            return _findingByItem.ContainsKey(itemId);
        }
    }

    /// <summary>Returns copies of all findings matching a predicate.</summary>
    /// <param name="predicate">Filter evaluated under the lock; must not block.</param>
    /// <returns>Copies.</returns>
    public List<Finding> QueryFindings(Func<Finding, bool> predicate)
    {
        lock (_sync)
        {
            return _findings.Values.Where(predicate).Select(Clone).ToList();
        }
    }

    /// <summary>Returns one sorted page of findings.</summary>
    /// <param name="predicate">Filter evaluated under the lock.</param>
    /// <param name="order">Sort.</param>
    /// <param name="skip">Skip.</param>
    /// <param name="take">Take.</param>
    /// <param name="total">Total matching.</param>
    /// <returns>Copies of the page.</returns>
    public List<Finding> QueryFindingsPage(Func<Finding, bool> predicate, Func<IEnumerable<Finding>, IOrderedEnumerable<Finding>> order, int skip, int take, out int total)
    {
        ArgumentNullException.ThrowIfNull(order);
        lock (_sync)
        {
            var matching = _findings.Values.Where(predicate).ToList();
            total = matching.Count;
            return order(matching).Skip(skip).Take(take).Select(Clone).ToList();
        }
    }

    /// <summary>Counts findings by status.</summary>
    /// <returns>Counts.</returns>
    public Dictionary<FindingStatus, int> CountByStatus()
    {
        lock (_sync)
        {
            return _findings.Values.GroupBy(f => f.Status).ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>
    /// Inserts or replaces the finding for an item; bumps its version.
    /// </summary>
    /// <param name="finding">Finding (copied).</param>
    /// <returns>Stored copy.</returns>
    public Finding UpsertFinding(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        lock (_sync)
        {
            var copy = Clone(finding);
            if (_findingByItem.TryGetValue(copy.ItemId, out var existingId) && _findings.TryGetValue(existingId, out var existing))
            {
                copy.ReviewId = existing.ReviewId;
                copy.FirstSeenUtc = existing.FirstSeenUtc;
                copy.Version = existing.Version + 1;
            }
            else
            {
                if (copy.ReviewId == Guid.Empty)
                {
                    copy.ReviewId = Guid.NewGuid();
                }

                copy.Version = 1;
                if (copy.FirstSeenUtc == default)
                {
                    copy.FirstSeenUtc = DateTime.UtcNow;
                }
            }

            copy.UpdatedUtc = DateTime.UtcNow;
            _findings[copy.ReviewId] = copy;
            _findingByItem[copy.ItemId] = copy.ReviewId;
            _dirty.Add(FindingsFile);
            EnforceFindingCap();
            return Clone(copy);
        }
    }

    /// <summary>
    /// Mutates a finding under optimistic concurrency.
    /// </summary>
    /// <param name="reviewId">Review id.</param>
    /// <param name="expectedVersion">Expected version, or null to skip the check.</param>
    /// <param name="mutate">Mutation; return false to abort without saving.</param>
    /// <param name="result">Updated copy.</param>
    /// <returns>NotFound, VersionMismatch, Aborted or Updated.</returns>
    public UpdateResult TryUpdateFinding(Guid reviewId, int? expectedVersion, Func<Finding, bool> mutate, out Finding? result)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_sync)
        {
            result = null;
            if (!_findings.TryGetValue(reviewId, out var existing))
            {
                return UpdateResult.NotFound;
            }

            if (expectedVersion is not null && existing.Version != expectedVersion.Value)
            {
                result = Clone(existing);
                return UpdateResult.VersionMismatch;
            }

            var working = Clone(existing);
            if (!mutate(working))
            {
                result = Clone(existing);
                return UpdateResult.Aborted;
            }

            working.ReviewId = existing.ReviewId;
            working.ItemId = existing.ItemId;
            working.Version = existing.Version + 1;
            working.UpdatedUtc = DateTime.UtcNow;
            _findings[reviewId] = working;
            _dirty.Add(FindingsFile);
            result = Clone(working);
            return UpdateResult.Updated;
        }
    }

    /// <summary>Removes a finding.</summary>
    /// <param name="reviewId">Review id.</param>
    public void RemoveFinding(Guid reviewId)
    {
        lock (_sync)
        {
            if (_findings.Remove(reviewId, out var f))
            {
                _findingByItem.Remove(f.ItemId);
                _dirty.Add(FindingsFile);
            }
        }
    }

    // ------------------------------------------------------------ decisions

    /// <summary>Appends a decision and applies retention.</summary>
    /// <param name="decision">Decision.</param>
    /// <param name="retentionDays">Retention days.</param>
    /// <param name="maxDecisions">Maximum decisions.</param>
    public void AddDecision(DecisionRecord decision, int retentionDays, int maxDecisions)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_sync)
        {
            var index = _decisions.FindIndex(d => d.DecisionId == decision.DecisionId);
            if (index >= 0)
            {
                _decisions[index] = Clone(decision);
            }
            else
            {
                _decisions.Add(Clone(decision));
            }

            ApplyRetention(retentionDays, maxDecisions);
            _dirty.Add(DecisionsFile);
        }
    }

    /// <summary>Gets a decision.</summary>
    /// <param name="decisionId">Decision id.</param>
    /// <returns>Copy or null.</returns>
    public DecisionRecord? GetDecision(Guid decisionId)
    {
        lock (_sync)
        {
            var d = _decisions.Find(x => x.DecisionId == decisionId);
            return d is null ? null : Clone(d);
        }
    }

    /// <summary>Lists decisions newest first.</summary>
    /// <param name="reviewId">Optional review filter.</param>
    /// <param name="skip">Skip.</param>
    /// <param name="take">Take.</param>
    /// <param name="total">Total matching.</param>
    /// <returns>Copies.</returns>
    public List<DecisionRecord> GetDecisions(Guid? reviewId, int skip, int take, out int total)
    {
        lock (_sync)
        {
            var q = _decisions.Where(d => reviewId is null || d.ReviewId == reviewId.Value).OrderByDescending(d => d.TimeUtc).ToList();
            total = q.Count;
            return q.Skip(skip).Take(take).Select(Clone).ToList();
        }
    }

    // ------------------------------------------------------------- coverage

    /// <summary>Gets coverage for an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Copy or null.</returns>
    public CoverageEntry? GetCoverage(Guid itemId)
    {
        lock (_sync)
        {
            return _coverage.TryGetValue(itemId, out var c) ? Clone(c) : null;
        }
    }

    /// <summary>Sets coverage for an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="entry">Entry.</param>
    public void SetCoverage(Guid itemId, CoverageEntry entry)
    {
        lock (_sync)
        {
            _coverage[itemId] = Clone(entry);
            _dirty.Add(CoverageFile);
        }
    }

    /// <summary>Removes coverage entries (and open findings) for items that no longer exist.</summary>
    /// <param name="existing">Ids of all current candidate items in scope libraries.</param>
    /// <param name="libraryIds">Libraries that were enumerated.</param>
    /// <returns>Number of removed coverage entries.</returns>
    public int PruneMissing(IReadOnlySet<Guid> existing, IReadOnlySet<Guid> libraryIds)
    {
        lock (_sync)
        {
            var gone = _coverage.Where(kv => libraryIds.Contains(kv.Value.LibraryId) && !existing.Contains(kv.Key)).Select(kv => kv.Key).ToList();
            foreach (var id in gone)
            {
                _coverage.Remove(id);
                if (_findingByItem.TryGetValue(id, out var reviewId) && _findings.TryGetValue(reviewId, out var f)
                    && f.Status is not (FindingStatus.Applied or FindingStatus.Partial or FindingStatus.RolledBack))
                {
                    _findings.Remove(reviewId);
                    _findingByItem.Remove(id);
                    _dirty.Add(FindingsFile);
                }
            }

            if (gone.Count > 0)
            {
                _dirty.Add(CoverageFile);
            }

            return gone.Count;
        }
    }

    /// <summary>Returns coverage statistics for the given libraries.</summary>
    /// <param name="libraryIds">Library ids.</param>
    /// <returns>Count and oldest/newest check times.</returns>
    public (int Count, DateTime? Newest) CoverageStats(IReadOnlySet<Guid> libraryIds)
    {
        lock (_sync)
        {
            var entries = _coverage.Values.Where(c => libraryIds.Contains(c.LibraryId)).ToList();
            return (entries.Count, entries.Count == 0 ? null : entries.Max(e => e.LastCheckedUtc));
        }
    }

    /// <summary>Returns ids with coverage.</summary>
    /// <returns>Ids.</returns>
    public HashSet<Guid> CoveredItemIds()
    {
        lock (_sync)
        {
            return _coverage.Keys.ToHashSet();
        }
    }

    // ---------------------------------------------------------------- queue

    /// <summary>
    /// Adds or coalesces a queue entry. A later event pushes the debounce window forward.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="origin">Origin.</param>
    /// <param name="runId">Audit run.</param>
    /// <param name="notBeforeUtc">Earliest processing time.</param>
    /// <param name="force">Ignore the dedupe fingerprint.</param>
    /// <returns>False when the queue is full (a reconciliation is scheduled instead).</returns>
    public bool Enqueue(Guid itemId, FindingOrigin origin, Guid? runId, DateTime notBeforeUtc, bool force)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (_queue.TryGetValue(itemId, out var existing))
            {
                existing.LastEventUtc = now;
                if (notBeforeUtc > existing.NotBeforeUtc)
                {
                    existing.NotBeforeUtc = notBeforeUtc;
                }

                existing.Force |= force;

                // A scan event outranks other attributions (the item is new, not merely existing);
                // otherwise the first reason for queuing the item is kept.
                if (origin == FindingOrigin.NewScan || origin == FindingOrigin.Manual)
                {
                    existing.Origin = origin;
                }

                // Only attribute to a run work that will happen when the run asked for it; an entry
                // waiting for a provider back-off must not hold the run open.
                if (existing.RunId is null && existing.NotBeforeUtc <= notBeforeUtc)
                {
                    existing.RunId = runId;
                }

                _dirty.Add(QueueFile);
                return true;
            }

            if (_queue.Count >= MaxQueue)
            {
                _state.ReconciliationRequired = true;
                _dirty.Add(StateFile);
                return false;
            }

            _queue[itemId] = new QueueEntry
            {
                ItemId = itemId,
                Origin = origin,
                RunId = runId,
                EnqueuedUtc = now,
                LastEventUtc = now,
                NotBeforeUtc = notBeforeUtc,
                Force = force,
            };
            _dirty.Add(QueueFile);
            return true;
        }
    }

    /// <summary>Returns entries ready to process, oldest first.</summary>
    /// <param name="nowUtc">Now.</param>
    /// <param name="max">Maximum.</param>
    /// <returns>Copies.</returns>
    public List<QueueEntry> PeekReady(DateTime nowUtc, int max)
    {
        lock (_sync)
        {
            return _queue.Values.Where(q => q.NotBeforeUtc <= nowUtc).OrderBy(q => q.NotBeforeUtc).Take(max).Select(Clone).ToList();
        }
    }

    /// <summary>Gets the next due time.</summary>
    /// <returns>Earliest NotBefore or null when empty.</returns>
    public DateTime? NextDueUtc()
    {
        lock (_sync)
        {
            return _queue.Count == 0 ? null : _queue.Values.Min(q => q.NotBeforeUtc);
        }
    }

    /// <summary>Removes a queue entry if it was not touched since <paramref name="peekedLastEvent"/>.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="peekedLastEvent">LastEventUtc observed when the entry was taken.</param>
    /// <returns>True when removed; false when a newer event arrived meanwhile.</returns>
    public bool CompleteQueueEntry(Guid itemId, DateTime peekedLastEvent)
    {
        lock (_sync)
        {
            if (_queue.TryGetValue(itemId, out var e) && e.LastEventUtc <= peekedLastEvent)
            {
                _queue.Remove(itemId);
                _dirty.Add(QueueFile);
                return true;
            }

            return false;
        }
    }

    /// <summary>Defers a queue entry.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="notBeforeUtc">New earliest time.</param>
    public void Defer(Guid itemId, DateTime notBeforeUtc)
    {
        lock (_sync)
        {
            if (_queue.TryGetValue(itemId, out var e))
            {
                e.NotBeforeUtc = notBeforeUtc;
                e.Deferrals++;
                _dirty.Add(QueueFile);
            }
        }
    }

    /// <summary>
    /// Detaches a queue entry from its run and schedules it for later (provider back-off), so the
    /// run can complete while the retry stays queued.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="notBeforeUtc">Retry time.</param>
    public void DetachAndDefer(Guid itemId, DateTime notBeforeUtc)
    {
        lock (_sync)
        {
            if (_queue.TryGetValue(itemId, out var e))
            {
                e.RunId = null;
                e.Force = false;
                e.NotBeforeUtc = notBeforeUtc;
                _dirty.Add(QueueFile);
            }
        }
    }

    /// <summary>Counts entries due now and entries deferred to later.</summary>
    /// <param name="nowUtc">Now.</param>
    /// <returns>Due and deferred counts.</returns>
    public (int Due, int Deferred) QueueDueCounts(DateTime nowUtc)
    {
        lock (_sync)
        {
            var due = _queue.Values.Count(q => q.NotBeforeUtc <= nowUtc);
            return (due, _queue.Count - due);
        }
    }

    /// <summary>Gets queue length.</summary>
    /// <returns>Count.</returns>
    public int QueueCount()
    {
        lock (_sync)
        {
            return _queue.Count;
        }
    }

    /// <summary>Counts queued entries for a run.</summary>
    /// <param name="runId">Run id.</param>
    /// <returns>Count.</returns>
    public int QueueCountForRun(Guid runId)
    {
        lock (_sync)
        {
            return _queue.Values.Count(q => q.RunId == runId);
        }
    }

    /// <summary>Counts queued entries by origin.</summary>
    /// <returns>Counts.</returns>
    public Dictionary<FindingOrigin, int> QueueCountByOrigin()
    {
        lock (_sync)
        {
            return _queue.Values.GroupBy(q => q.Origin).ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>Returns queued item ids.</summary>
    /// <returns>Ids.</returns>
    public HashSet<Guid> QueuedItemIds()
    {
        lock (_sync)
        {
            return _queue.Keys.ToHashSet();
        }
    }

    /// <summary>Removes queued entries that belong to a run and have no other reason to stay.</summary>
    /// <param name="runId">Run id.</param>
    /// <returns>Removed count.</returns>
    public int RemoveRunEntries(Guid runId)
    {
        lock (_sync)
        {
            var ids = _queue.Values.Where(q => q.RunId == runId && q.Origin != FindingOrigin.NewScan).Select(q => q.ItemId).ToList();
            foreach (var id in ids)
            {
                _queue.Remove(id);
            }

            foreach (var q in _queue.Values.Where(q => q.RunId == runId))
            {
                q.RunId = null;
            }

            _dirty.Add(QueueFile);
            return ids.Count;
        }
    }

    // ----------------------------------------------------------------- runs

    /// <summary>Saves an audit run.</summary>
    /// <param name="run">Run.</param>
    public void SaveAuditRun(AuditRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_sync)
        {
            var i = _runs.AuditRuns.FindIndex(r => r.RunId == run.RunId);
            if (i >= 0)
            {
                _runs.AuditRuns[i] = Clone(run);
            }
            else
            {
                _runs.AuditRuns.Add(Clone(run));
            }

            if (_runs.AuditRuns.Count > 50)
            {
                _runs.AuditRuns = _runs.AuditRuns.OrderByDescending(r => r.StartedAtUtc).Take(50).ToList();
            }

            _dirty.Add(RunsFile);
        }
    }

    /// <summary>Mutates an audit run.</summary>
    /// <param name="runId">Run id.</param>
    /// <param name="mutate">Mutation.</param>
    /// <returns>Updated copy or null.</returns>
    public AuditRun? UpdateAuditRun(Guid runId, Action<AuditRun> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_sync)
        {
            var run = _runs.AuditRuns.Find(r => r.RunId == runId);
            if (run is null)
            {
                return null;
            }

            mutate(run);
            _dirty.Add(RunsFile);
            return Clone(run);
        }
    }

    /// <summary>Gets an audit run.</summary>
    /// <param name="runId">Run id.</param>
    /// <returns>Copy or null.</returns>
    public AuditRun? GetAuditRun(Guid runId)
    {
        lock (_sync)
        {
            var r = _runs.AuditRuns.Find(x => x.RunId == runId);
            return r is null ? null : Clone(r);
        }
    }

    /// <summary>Lists audit runs, newest first.</summary>
    /// <returns>Copies.</returns>
    public List<AuditRun> GetAuditRuns()
    {
        lock (_sync)
        {
            return _runs.AuditRuns.OrderByDescending(r => r.StartedAtUtc).Select(Clone).ToList();
        }
    }

    /// <summary>Saves a batch run.</summary>
    /// <param name="run">Run.</param>
    public void SaveBatchRun(BatchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_sync)
        {
            var i = _runs.BatchRuns.FindIndex(r => r.RunId == run.RunId);
            if (i >= 0)
            {
                _runs.BatchRuns[i] = Clone(run);
            }
            else
            {
                _runs.BatchRuns.Add(Clone(run));
            }

            if (_runs.BatchRuns.Count > 50)
            {
                _runs.BatchRuns = _runs.BatchRuns.OrderByDescending(r => r.StartedAtUtc).Take(50).ToList();
            }

            _dirty.Add(RunsFile);
        }
    }

    /// <summary>Mutates a batch run.</summary>
    /// <param name="runId">Run id.</param>
    /// <param name="mutate">Mutation.</param>
    /// <returns>Updated copy or null.</returns>
    public BatchRun? UpdateBatchRun(Guid runId, Action<BatchRun> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_sync)
        {
            var run = _runs.BatchRuns.Find(r => r.RunId == runId);
            if (run is null)
            {
                return null;
            }

            mutate(run);
            _dirty.Add(RunsFile);
            return Clone(run);
        }
    }

    /// <summary>Gets a batch run.</summary>
    /// <param name="runId">Run id.</param>
    /// <returns>Copy or null.</returns>
    public BatchRun? GetBatchRun(Guid runId)
    {
        lock (_sync)
        {
            var r = _runs.BatchRuns.Find(x => x.RunId == runId);
            return r is null ? null : Clone(r);
        }
    }

    /// <summary>Finds a batch run by idempotency key.</summary>
    /// <param name="key">Key.</param>
    /// <returns>Copy or null.</returns>
    public BatchRun? FindBatchRunByKey(string key)
    {
        lock (_sync)
        {
            var r = _runs.BatchRuns.Find(x => string.Equals(x.IdempotencyKey, key, StringComparison.Ordinal));
            return r is null ? null : Clone(r);
        }
    }

    /// <summary>Lists batch runs, newest first.</summary>
    /// <returns>Copies.</returns>
    public List<BatchRun> GetBatchRuns()
    {
        lock (_sync)
        {
            return _runs.BatchRuns.OrderByDescending(r => r.StartedAtUtc).Select(Clone).ToList();
        }
    }

    // ---------------------------------------------------------------- flush

    /// <summary>
    /// Writes every dirty file atomically.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        List<(string Name, byte[] Bytes)> pending;
        lock (_sync)
        {
            if (_dirty.Count == 0)
            {
                return;
            }

            pending = new List<(string, byte[])>();
            foreach (var name in _dirty)
            {
                pending.Add((name, SerializeLocked(name)));
            }

            _dirty.Clear();
        }

        foreach (var (name, bytes) in pending)
        {
            try
            {
                await _files.WriteAsync(name, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Smart Match could not write journal file {File}; it will be retried", name);
                lock (_sync)
                {
                    _dirty.Add(name);
                }
            }
        }
    }

    private static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, JsonFileStore.Options), JsonFileStore.Options)!;

    private byte[] SerializeLocked(string name) => name switch
    {
        StateFile => JsonFileStore.Serialize(_state),
        FindingsFile => JsonFileStore.Serialize(_findings.Values.ToList()),
        DecisionsFile => JsonFileStore.Serialize(_decisions),
        CoverageFile => JsonFileStore.Serialize(_coverage),
        QueueFile => JsonFileStore.Serialize(_queue),
        RunsFile => JsonFileStore.Serialize(_runs),
        _ => throw new InvalidOperationException("Unknown journal file " + name),
    };

    private T? LoadFile<T>(string name)
        where T : class
    {
        var data = _files.Load<T>(name, out var warning);
        if (warning is not null)
        {
            _loadWarnings.Add(warning);
            _dirty.Add(name);
        }

        return data;
    }

    private void ApplyRetention(int retentionDays, int maxDecisions)
    {
        // Decisions still needed for rollback of an applied finding are pinned.
        var pinned = _findings.Values
            .Where(f => f.Status is FindingStatus.Applied or FindingStatus.Partial && f.LastDecisionId is not null)
            .Select(f => f.LastDecisionId!.Value)
            .ToHashSet();
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        _decisions = _decisions
            .Where(d => pinned.Contains(d.DecisionId) || d.TimeUtc >= cutoff)
            .OrderByDescending(d => d.TimeUtc)
            .Where((d, index) => index < maxDecisions || pinned.Contains(d.DecisionId))
            .OrderBy(d => d.TimeUtc)
            .ToList();
    }

    private void EnforceFindingCap()
    {
        if (_findings.Count <= MaxFindings)
        {
            return;
        }

        var removable = _findings.Values
            .Where(f => f.Status is FindingStatus.Resolved or FindingStatus.NoMatch or FindingStatus.RolledBack)
            .OrderBy(f => f.UpdatedUtc)
            .Take(_findings.Count - MaxFindings)
            .ToList();
        foreach (var f in removable)
        {
            _findings.Remove(f.ReviewId);
            _findingByItem.Remove(f.ItemId);
        }
    }

    /// <summary>
    /// Audit and batch run document.
    /// </summary>
    public sealed class RunsDocument
    {
        /// <summary>Gets or sets audit runs.</summary>
        public List<AuditRun> AuditRuns { get; set; } = new();

        /// <summary>Gets or sets batch runs.</summary>
        public List<BatchRun> BatchRuns { get; set; } = new();
    }
}

/// <summary>
/// Result of <see cref="SmartMatchStore.TryUpdateFinding"/>.
/// </summary>
public enum UpdateResult
{
    /// <summary>Updated.</summary>
    Updated,

    /// <summary>No such finding.</summary>
    NotFound,

    /// <summary>Version mismatch (stale UI or concurrent change).</summary>
    VersionMismatch,

    /// <summary>Mutation declined.</summary>
    Aborted,
}
