using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Configuration;
using Jellyfin.Plugin.SmartMatch.Discovery;
using Jellyfin.Plugin.SmartMatch.Library;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Normalization;
using Jellyfin.Plugin.SmartMatch.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartMatch.Services;

/// <summary>
/// Report filter shared by the review list and "Apply all eligible".
/// </summary>
public sealed class FindingFilter
{
    /// <summary>Gets or sets statuses (empty = all).</summary>
    public List<FindingStatus> Statuses { get; set; } = new();

    /// <summary>Gets or sets the library.</summary>
    public Guid? LibraryId { get; set; }

    /// <summary>Gets or sets the media type.</summary>
    public MediaKind? MediaType { get; set; }

    /// <summary>Gets or sets the minimum score of the recommended candidate.</summary>
    public int? MinScore { get; set; }

    /// <summary>Gets or sets the confidence filter: "auto" (auto-eligible), "high" (score ≥ 90), "low".</summary>
    public string? Confidence { get; set; }

    /// <summary>Gets or sets a title search (case-insensitive substring of original, search or candidate title).</summary>
    public string? Search { get; set; }

    /// <summary>Gets or sets the origin.</summary>
    public FindingOrigin? Origin { get; set; }

    /// <summary>Gets a value indicating whether the filter restricts anything.</summary>
    public bool IsEmpty => Statuses.Count == 0 && LibraryId is null && MediaType is null && MinScore is null && string.IsNullOrWhiteSpace(Confidence) && string.IsNullOrWhiteSpace(Search) && Origin is null;

    /// <summary>Evaluates the filter.</summary>
    /// <param name="f">Finding.</param>
    /// <returns>True when matching.</returns>
    public bool Matches(Finding f)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (Statuses.Count > 0 && !Statuses.Contains(f.Status))
        {
            return false;
        }

        if (LibraryId is { } lib && f.LibraryId != lib)
        {
            return false;
        }

        if (MediaType is { } mt && f.MediaType != mt)
        {
            return false;
        }

        if (Origin is { } origin && f.Origin != origin)
        {
            return false;
        }

        var top = f.Recommended();
        if (MinScore is { } min && (top?.RankScore ?? 0) < min)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Confidence))
        {
            var ok = Confidence.ToLowerInvariant() switch
            {
                "auto" => top?.AutoEligible == true,
                "high" => (top?.RankScore ?? 0) >= 90,
                "medium" => (top?.RankScore ?? 0) is >= 70 and < 90,
                "low" => (top?.RankScore ?? 0) < 70,
                _ => true,
            };
            if (!ok)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            var hit = f.OriginalName.Contains(s, StringComparison.OrdinalIgnoreCase)
                || f.SearchTitle.Contains(s, StringComparison.OrdinalIgnoreCase)
                || f.SourceName.Contains(s, StringComparison.OrdinalIgnoreCase)
                || f.Candidates.Any(c => c.Name.Contains(s, StringComparison.OrdinalIgnoreCase));
            if (!hit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Human-readable description used in previews.</summary>
    /// <returns>Description.</returns>
    public string Describe()
    {
        if (IsEmpty)
        {
            return "entire report";
        }

        var parts = new List<string>();
        if (Statuses.Count > 0)
        {
            parts.Add("status " + string.Join("/", Statuses));
        }

        if (LibraryId is not null)
        {
            parts.Add("one library");
        }

        if (MediaType is not null)
        {
            parts.Add("type " + MediaType);
        }

        if (Origin is not null)
        {
            parts.Add("origin " + Origin);
        }

        if (!string.IsNullOrWhiteSpace(Confidence))
        {
            parts.Add("confidence " + Confidence);
        }

        if (MinScore is not null)
        {
            parts.Add("score ≥ " + MinScore);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            parts.Add("search \"" + Search.Trim() + "\"");
        }

        return "current filter: " + string.Join(", ", parts);
    }
}

/// <summary>
/// Preview request.
/// </summary>
public sealed class BatchPreviewRequest
{
    /// <summary>Gets or sets the scope: "Selected" or "AllEligible". Never implicit.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected review ids (Selected scope).</summary>
    public List<Guid> ReviewIds { get; set; } = new();

    /// <summary>Gets or sets the filter (AllEligible scope); null or empty means the entire report.</summary>
    public FindingFilter? Filter { get; set; }
}

/// <summary>
/// An item excluded from a plan.
/// </summary>
/// <param name="ReviewId">Finding.</param>
/// <param name="Title">Title.</param>
/// <param name="Status">Status.</param>
/// <param name="Reason">Reason.</param>
public sealed record PlanExclusion(Guid ReviewId, string Title, string Status, string Reason);

/// <summary>
/// An item a plan would write.
/// </summary>
/// <param name="ReviewId">Finding.</param>
/// <param name="ExpectedVersion">Version.</param>
/// <param name="Title">Original title.</param>
/// <param name="CandidateId">Candidate.</param>
/// <param name="CandidateName">Candidate name.</param>
/// <param name="CandidateYear">Candidate year.</param>
/// <param name="ProviderIds">Ids that will be written.</param>
/// <param name="RankScore">Score.</param>
/// <param name="WritesTitle">True when the title will be replaced.</param>
/// <param name="Warnings">Warnings (e.g. sidecar saving).</param>
public sealed record PlanItem(Guid ReviewId, int ExpectedVersion, string Title, string CandidateId, string CandidateName, int? CandidateYear, IReadOnlyDictionary<string, string> ProviderIds, int RankScore, bool WritesTitle, IReadOnlyList<string> Warnings);

/// <summary>
/// A short-lived, server-held apply plan.
/// </summary>
/// <param name="PlanId">Plan id.</param>
/// <param name="PlanHash">Hash the client must echo at commit.</param>
/// <param name="Scope">Selected or AllEligible.</param>
/// <param name="ScopeDescription">Exact scope shown to the admin.</param>
/// <param name="CreatedUtc">Created.</param>
/// <param name="ExpiresUtc">Expiry.</param>
/// <param name="Items">Items to apply.</param>
/// <param name="Exclusions">Excluded items with reasons.</param>
/// <param name="ExclusionSummary">Exclusion counts by reason.</param>
/// <param name="Actor">Admin who previewed.</param>
public sealed record BatchPlan(Guid PlanId, string PlanHash, string Scope, string ScopeDescription, DateTime CreatedUtc, DateTime ExpiresUtc, IReadOnlyList<PlanItem> Items, IReadOnlyList<PlanExclusion> Exclusions, IReadOnlyDictionary<string, int> ExclusionSummary, string Actor);

/// <summary>
/// Error from a batch operation.
/// </summary>
public sealed class BatchException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BatchException"/> class.
    /// </summary>
    /// <param name="code">Code.</param>
    /// <param name="message">Message.</param>
    public BatchException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchException"/> class.
    /// </summary>
    public BatchException()
    {
        Code = "ERROR";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    public BatchException(string message)
        : base(message)
    {
        Code = "ERROR";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Inner.</param>
    public BatchException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "ERROR";
    }

    /// <summary>Gets the code.</summary>
    public string Code { get; }
}

/// <summary>
/// Preview → confirm → execute protocol for Apply selected and Apply all eligible.
/// Plans are short-lived and bound to the exact finding versions previewed; every item is
/// revalidated again by <see cref="MatchApplicationService"/> right before its write.
/// </summary>
public sealed class BulkApplyCoordinator
{
    /// <summary>Plan lifetime.</summary>
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Maximum items in one plan.</summary>
    public const int MaxPlanItems = 5000;

    private readonly ConcurrentDictionary<Guid, BatchPlan> _plans = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();
    private readonly SmartMatchStore _store;
    private readonly MatchApplicationService _apply;
    private readonly ILibraryGateway _library;
    private readonly IConfigProvider _config;
    private readonly TimeProvider _time;
    private readonly ILogger<BulkApplyCoordinator> _logger;
    private readonly object _commitSync = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BulkApplyCoordinator"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="apply">Apply service.</param>
    /// <param name="library">Library gateway.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public BulkApplyCoordinator(SmartMatchStore store, MatchApplicationService apply, ILibraryGateway library, IConfigProvider config, TimeProvider time, ILogger<BulkApplyCoordinator> logger)
    {
        _store = store;
        _apply = apply;
        _library = library;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Builds a plan. No Jellyfin mutation.
    /// </summary>
    /// <param name="request">Request.</param>
    /// <param name="actor">Admin.</param>
    /// <returns>Plan.</returns>
    public BatchPlan Preview(BatchPreviewRequest request, string actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        PurgeExpired();
        var scope = request.Scope?.Trim() ?? string.Empty;
        List<Finding> scoped;
        string description;
        if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
        {
            var ids = request.ReviewIds.Distinct().ToList();
            if (ids.Count == 0)
            {
                throw new BatchException("EMPTY_SELECTION", "Select at least one finding.");
            }

            if (ids.Count > MaxPlanItems)
            {
                throw new BatchException("TOO_MANY", $"At most {MaxPlanItems} findings per batch.");
            }

            var set = ids.ToHashSet();
            scoped = _store.QueryFindings(f => set.Contains(f.ReviewId));
            description = $"{ids.Count} selected finding(s)";
            scope = "Selected";
        }
        else if (string.Equals(scope, "AllEligible", StringComparison.OrdinalIgnoreCase))
        {
            var filter = request.Filter ?? new FindingFilter();
            scoped = _store.QueryFindings(filter.Matches);
            description = "all eligible findings in the " + filter.Describe();
            scope = "AllEligible";
        }
        else
        {
            throw new BatchException("SCOPE_REQUIRED", "Scope must be 'Selected' or 'AllEligible'.");
        }

        var config = _config.Current;
        var libraries = _library.GetLibraries();
        var inScope = LibraryScope.InScope(libraries, config);
        var validator = new ProviderIdValidator(config.ExtraProviderIdKeys);
        var salt = _store.GetState().PathSalt;
        var items = new List<PlanItem>();
        var exclusions = new List<PlanExclusion>();

        if (scope == "Selected")
        {
            foreach (var missing in request.ReviewIds.Distinct().Where(id => scoped.All(f => f.ReviewId != id)))
            {
                exclusions.Add(new PlanExclusion(missing, "(unknown)", "Missing", "Finding no longer exists."));
            }
        }

        foreach (var f in scoped.OrderBy(f => f.OriginalName, StringComparer.OrdinalIgnoreCase))
        {
            var reason = ExclusionReason(f, inScope);
            CandidateRecord? top = null;
            if (reason is null)
            {
                top = f.Recommended();
                if (top is null || !top.Applicable)
                {
                    reason = "No applicable recommended candidate.";
                }
            }

            ItemSnapshot? item = null;
            if (reason is null)
            {
                item = _library.GetItem(f.ItemId);
                reason = item switch
                {
                    null => "Item no longer exists.",
                    { IsLocked: true } => "Item metadata is locked.",
                    _ when validator.HasReliableIdentity(item.ProviderIds) => "Item already has a provider identity.",
                    _ when Fingerprints.Path(salt, item.Path) != f.PathHash => "Media path changed since analysis (stale).",
                    _ when Fingerprints.Item(item) != f.ItemFingerprint => "Item changed since analysis (stale); retry analysis first.",
                    _ => null,
                };
            }

            if (reason is not null)
            {
                exclusions.Add(new PlanExclusion(f.ReviewId, f.OriginalName, f.Status.ToString(), reason));
                continue;
            }

            var warnings = new List<string>();
            var library = libraries.FirstOrDefault(l => l.Id == f.LibraryId);
            if (library?.SidecarRisk == true)
            {
                warnings.Add("Jellyfin may write NFO/image files next to the media during refresh: " + library.SidecarDetail);
            }

            if (item!.HasLocalNfo)
            {
                warnings.Add("A local NFO file exists for this item.");
            }

            var writesTitle = config.WriteCanonicalTitle && !item.NameLocked && item.Name == f.OriginalName && item.Name != top!.Name;
            items.Add(new PlanItem(f.ReviewId, f.Version, f.OriginalName, top!.CandidateId, top.Name, top.Year, top.ProviderIds, top.RankScore, writesTitle, warnings));
        }

        if (items.Count > MaxPlanItems)
        {
            throw new BatchException("TOO_MANY", $"The scope contains {items.Count} eligible findings; narrow the filter to at most {MaxPlanItems}.");
        }

        var summary = exclusions.GroupBy(e => e.Reason).ToDictionary(g => g.Key, g => g.Count());
        var now = _time.GetUtcNow().UtcDateTime;
        var hash = Fingerprints.Hash(string.Join('\n', items.Select(i => $"{i.ReviewId}:{i.CandidateId}:{i.ExpectedVersion}")), 32);
        var plan = new BatchPlan(Guid.NewGuid(), hash, scope, description, now, now + PlanLifetime, items, exclusions.Take(500).ToList(), summary, actor);
        _plans[plan.PlanId] = plan;
        return plan;
    }

    /// <summary>
    /// Confirms a plan and starts executing it in the background.
    /// </summary>
    /// <param name="planId">Plan.</param>
    /// <param name="planHash">Hash shown at preview.</param>
    /// <param name="idempotencyKey">Client key; replays return the existing run.</param>
    /// <param name="actor">Admin.</param>
    /// <returns>The run.</returns>
    public BatchRun Commit(Guid planId, string planHash, string idempotencyKey, string actor)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            throw new BatchException("IDEMPOTENCY_KEY_REQUIRED", "An idempotency key (1-100 characters) is required.");
        }

        lock (_commitSync)
        {
            var replay = _store.FindBatchRunByKey(idempotencyKey);
            if (replay is not null)
            {
                if (replay.PlanId != planId)
                {
                    throw new BatchException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was used for a different plan.");
                }

                return replay;
            }

            if (!_plans.TryGetValue(planId, out var plan) || plan.ExpiresUtc < _time.GetUtcNow().UtcDateTime)
            {
                _plans.TryRemove(planId, out _);
                throw new BatchException("PLAN_EXPIRED", "The preview expired or was already used; preview again.");
            }

            if (!string.Equals(plan.PlanHash, planHash, StringComparison.Ordinal))
            {
                throw new BatchException("PLAN_CHANGED", "The plan does not match the preview that was confirmed; preview again.");
            }

            if (plan.Items.Count == 0)
            {
                throw new BatchException("NOTHING_TO_APPLY", "The plan contains no eligible findings.");
            }

            _plans.TryRemove(planId, out _);
            var run = new BatchRun
            {
                RunId = Guid.NewGuid(),
                PlanId = planId,
                IdempotencyKey = idempotencyKey,
                Scope = plan.ScopeDescription,
                Actor = actor,
                StartedAtUtc = _time.GetUtcNow().UtcDateTime,
                Status = RunStatus.Running,
                Items = plan.Items.Select(i => new BatchItem { ReviewId = i.ReviewId, CandidateId = i.CandidateId, ExpectedVersion = i.ExpectedVersion, Title = i.Title }).ToList(),
            };
            _store.SaveBatchRun(run);
            var cts = new CancellationTokenSource();
            _running[run.RunId] = cts;
            _tasks[run.RunId] = Task.Run(() => ExecuteAsync(run.RunId, actor, cts.Token));
            _logger.LogInformation("Smart Match batch {RunId} started with {Count} item(s)", run.RunId, run.Items.Count);
            return run;
        }
    }

    /// <summary>Requests cancellation of future commits in a run.</summary>
    /// <param name="runId">Run.</param>
    /// <returns>True when the run was running.</returns>
    public bool Cancel(Guid runId)
    {
        if (_running.TryGetValue(runId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }

    /// <summary>Waits for running batches (shutdown).</summary>
    /// <param name="timeout">Timeout.</param>
    /// <returns>Task.</returns>
    public async Task StopAsync(TimeSpan timeout)
    {
        foreach (var cts in _running.Values)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(_tasks.Values).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Smart Match batch runs did not stop within {Timeout}", timeout);
        }
    }

    /// <summary>Waits for a run to finish (tests and scheduled callers).</summary>
    /// <param name="runId">Run.</param>
    /// <returns>Task.</returns>
    public Task WaitAsync(Guid runId) => _tasks.TryGetValue(runId, out var t) ? t : Task.CompletedTask;

    /// <summary>
    /// Marks runs that were executing when the server stopped as interrupted. Completed items keep
    /// their outcome; pending items were never started and stay in the report for a new preview.
    /// </summary>
    public void RecoverInterrupted()
    {
        foreach (var run in _store.GetBatchRuns().Where(r => r.Status == RunStatus.Running))
        {
            _store.UpdateBatchRun(run.RunId, r =>
            {
                r.Status = RunStatus.Interrupted;
                r.EndedAtUtc = DateTime.UtcNow;
                foreach (var item in r.Items.Where(i => i.Outcome is null))
                {
                    item.Outcome = ApplyOutcome.Cancelled;
                    item.Message = "Server restarted before this item started; preview again to retry.";
                }
            });
        }
    }

    private static string? ExclusionReason(Finding f, HashSet<Guid> inScope)
    {
        if (!inScope.Contains(f.LibraryId))
        {
            return "Library is not selected.";
        }

        return f.Status switch
        {
            FindingStatus.Proposed => null,
            FindingStatus.NeedsReview => "Ambiguous or weak match: review and choose a candidate individually.",
            FindingStatus.Applied => "Already applied.",
            FindingStatus.Ignored => "Ignored by an admin.",
            FindingStatus.Rejected => "Rejected by an admin.",
            FindingStatus.Conflict => "Conflict: retry analysis first.",
            FindingStatus.Partial => "Partially applied: use Retry on the item.",
            FindingStatus.NoMatch => "No match found.",
            FindingStatus.ProviderError => "Provider error.",
            _ => $"Status {f.Status} is not eligible.",
        };
    }

    private void PurgeExpired()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (id, plan) in _plans)
        {
            if (plan.ExpiresUtc < now)
            {
                _plans.TryRemove(id, out _);
            }
        }
    }

    private async Task ExecuteAsync(Guid runId, string actor, CancellationToken cancellationToken)
    {
        try
        {
            var run = _store.GetBatchRun(runId)!;
            foreach (var item in run.Items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                ApplyResult result;
                try
                {
                    result = await _apply.ApplyAsync(new ApplyRequest(item.ReviewId, item.CandidateId, item.ExpectedVersion, ApplyOrigin.Bulk, actor, runId), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Smart Match batch {RunId} item {ReviewId} failed", runId, item.ReviewId);
                    result = new ApplyResult(ApplyOutcome.Failed, "ERROR", ex.Message, null, null);
                }

                _store.UpdateBatchRun(runId, r =>
                {
                    var target = r.Items.First(i => i.ReviewId == item.ReviewId);
                    target.Outcome = result.Outcome;
                    target.Message = result.Code + ": " + result.Message;
                });
            }

            var cancelled = cancellationToken.IsCancellationRequested;
            _store.UpdateBatchRun(runId, r =>
            {
                foreach (var pending in r.Items.Where(i => i.Outcome is null))
                {
                    pending.Outcome = ApplyOutcome.Cancelled;
                    pending.Message = "Cancelled before this item started; it remains in the report.";
                }

                r.Status = cancelled ? RunStatus.Cancelled : RunStatus.Completed;
                r.EndedAtUtc = _time.GetUtcNow().UtcDateTime;
            });
            await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (_running.TryRemove(runId, out var cts))
            {
                cts.Dispose();
            }

            _tasks.TryRemove(runId, out _);
        }
    }
}
