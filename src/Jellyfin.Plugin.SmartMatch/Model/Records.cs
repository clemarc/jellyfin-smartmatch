using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartMatch.Normalization;

namespace Jellyfin.Plugin.SmartMatch.Model;

/// <summary>
/// A provider candidate stored with a finding (bounded to the top 10).
/// </summary>
public sealed class CandidateRecord
{
    /// <summary>Gets or sets the stable identifier, e.g. "tmdb:603".</summary>
    public string CandidateId { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the provider that returned the result first.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Gets or sets all distinct providers that returned this identity.</summary>
    public List<string> Providers { get; set; } = new();

    /// <summary>Gets or sets the validated provider identifiers that would be written.</summary>
    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the rank score out of 100 (not a probability).</summary>
    public int RankScore { get; set; }

    /// <summary>Gets or sets the title-similarity component of the score (0-70).</summary>
    public int TitleScore { get; set; }

    /// <summary>Gets or sets the scoring explanation.</summary>
    public List<string> Reasons { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the comparison keys are identical.</summary>
    public bool TitleExact { get; set; }

    /// <summary>Gets or sets a value indicating whether the year disagrees beyond tolerance.</summary>
    public bool YearConflict { get; set; }

    /// <summary>Gets or sets a value indicating whether the candidate has a writable identifier.</summary>
    public bool Applicable { get; set; }

    /// <summary>Gets or sets a value indicating whether the candidate passes the strict automatic gate.</summary>
    public bool AutoEligible { get; set; }
}

/// <summary>
/// A persistent row in the findings report.
/// </summary>
public sealed class Finding
{
    /// <summary>Gets or sets the review identifier.</summary>
    public Guid ReviewId { get; set; }

    /// <summary>Gets or sets the Jellyfin item identifier.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the library identifier.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>Gets or sets the library name.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the media kind.</summary>
    public MediaKind MediaType { get; set; }

    /// <summary>Gets or sets the original Jellyfin name.</summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>Gets or sets the basename that produced the search title (no directories).</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>Gets or sets the salted hash of the media path.</summary>
    public string PathHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the cleaned search title.</summary>
    public string SearchTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the comparison key.</summary>
    public string ComparisonKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the trusted year.</summary>
    public int? TrustedYear { get; set; }

    /// <summary>Gets or sets the extracted episode context.</summary>
    public string? EpisodeContext { get; set; }

    /// <summary>Gets or sets the provenance of the search title.</summary>
    public NameSourceKind SourceKind { get; set; }

    /// <summary>Gets or sets the normalization trace.</summary>
    public List<string> NormalizationTrace { get; set; } = new();

    /// <summary>Gets or sets warnings that block automation.</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Gets or sets the origin.</summary>
    public FindingOrigin Origin { get; set; }

    /// <summary>Gets or sets the review status.</summary>
    public FindingStatus Status { get; set; }

    /// <summary>Gets or sets a machine-readable reason or error code.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>Gets or sets a human-readable reason.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets the candidates, best first.</summary>
    public List<CandidateRecord> Candidates { get; set; } = new();

    /// <summary>Gets or sets the recommended candidate.</summary>
    public string? RecommendedCandidateId { get; set; }

    /// <summary>Gets or sets reasons the recommended candidate cannot be applied automatically.</summary>
    public List<string> AutoBlockers { get; set; } = new();

    /// <summary>Gets or sets the ranker revision that produced the classification (0 before 1.0.1).</summary>
    public int RankerRevision { get; set; }

    /// <summary>Gets or sets provider failures from the last search.</summary>
    public List<string> ProviderErrors { get; set; } = new();

    /// <summary>Gets or sets the item fingerprint at analysis time.</summary>
    public string ItemFingerprint { get; set; } = string.Empty;

    /// <summary>Gets or sets the query fingerprint (title, year, rules, providers).</summary>
    public string QueryFingerprint { get; set; } = string.Empty;

    /// <summary>Gets or sets the first time the item was reported.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Gets or sets the last analysis time.</summary>
    public DateTime LastCheckedUtc { get; set; }

    /// <summary>Gets or sets the last status change time.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Gets or sets the optimistic-concurrency version (ETag).</summary>
    public int Version { get; set; }

    /// <summary>Gets or sets the number of consecutive provider failures.</summary>
    public int ProviderFailures { get; set; }

    /// <summary>Gets or sets the time before which a provider-error finding is not retried.</summary>
    public DateTime? NextRetryUtc { get; set; }

    /// <summary>Gets or sets the candidate applied by the plugin.</summary>
    public string? AppliedCandidateId { get; set; }

    /// <summary>Gets or sets the last decision affecting this finding.</summary>
    public Guid? LastDecisionId { get; set; }

    /// <summary>Gets or sets the admin note for ignore/reject.</summary>
    public string? AdminNote { get; set; }

    /// <summary>Gets or sets a value indicating whether the library writes NFO/sidecar files on save.</summary>
    public bool SidecarRisk { get; set; }

    /// <summary>Gets the recommended candidate, if any.</summary>
    /// <returns>Candidate or null.</returns>
    public CandidateRecord? Recommended()
        => RecommendedCandidateId is null ? null : Candidates.Find(c => c.CandidateId == RecommendedCandidateId);
}

/// <summary>
/// Audit trail entry for every mutating action.
/// </summary>
public sealed class DecisionRecord
{
    /// <summary>Gets or sets the decision identifier.</summary>
    public Guid DecisionId { get; set; }

    /// <summary>Gets or sets the review identifier.</summary>
    public Guid ReviewId { get; set; }

    /// <summary>Gets or sets the item identifier.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name at decision time.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the acting admin user id (empty for automatic).</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Gets or sets the time.</summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>Gets or sets the action, e.g. Apply, Ignore, Reject, Rollback, RetryRefresh.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Gets or sets the origin of an apply.</summary>
    public ApplyOrigin? Origin { get; set; }

    /// <summary>Gets or sets the batch run identifier.</summary>
    public Guid? BatchRunId { get; set; }

    /// <summary>Gets or sets the candidate.</summary>
    public string? CandidateId { get; set; }

    /// <summary>Gets or sets provider identifiers before the write.</summary>
    public Dictionary<string, string> BeforeProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets provider identifiers written by the plugin.</summary>
    public Dictionary<string, string> WrittenProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the title before the write.</summary>
    public string? BeforeTitle { get; set; }

    /// <summary>Gets or sets the title written by the plugin (null when the title was not touched).</summary>
    public string? WrittenTitle { get; set; }

    /// <summary>Gets or sets the title observed after refresh.</summary>
    public string? ObservedAfterTitle { get; set; }

    /// <summary>Gets or sets provider identifiers observed after refresh.</summary>
    public Dictionary<string, string> ObservedAfterProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the outcome.</summary>
    public ApplyOutcome Outcome { get; set; }

    /// <summary>Gets or sets the error code.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the refresh outcome text.</summary>
    public string? RefreshOutcome { get; set; }

    /// <summary>Gets or sets the item fingerprint before the write.</summary>
    public string? ItemFingerprint { get; set; }
}

/// <summary>
/// One audit / reconciliation run.
/// </summary>
public sealed class AuditRun
{
    /// <summary>Gets or sets the run id.</summary>
    public Guid RunId { get; set; }

    /// <summary>Gets or sets the kind: InitialAudit, Audit, Reconciliation.</summary>
    public string Kind { get; set; } = "Audit";

    /// <summary>Gets or sets who started it.</summary>
    public string StartedBy { get; set; } = string.Empty;

    /// <summary>Gets or sets the start time.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Gets or sets the end time.</summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public RunStatus Status { get; set; }

    /// <summary>Gets or sets the apply mode at start.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin version.</summary>
    public string JellyfinVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the plugin version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether unchanged items are searched again.</summary>
    public bool Force { get; set; }

    /// <summary>Gets or sets the number of items enqueued.</summary>
    public int Enqueued { get; set; }

    /// <summary>Gets or sets per-outcome counters (Scanned, Skipped, Proposed, NeedsReview, NoMatch, ProviderError, Applied, Unchanged...).</summary>
    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the number of remote searches made.</summary>
    public int Searches { get; set; }

    /// <summary>Gets or sets skip reasons and counts.</summary>
    public Dictionary<string, int> SkipReasons { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets provider errors seen.</summary>
    public List<string> ProviderErrors { get; set; } = new();
}

/// <summary>
/// Per-item record of audit coverage (also the dedupe fingerprint store).
/// </summary>
public sealed class CoverageEntry
{
    /// <summary>Gets or sets the library id.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>Gets or sets the last processed item fingerprint.</summary>
    public string ItemFingerprint { get; set; } = string.Empty;

    /// <summary>Gets or sets the last query fingerprint.</summary>
    public string QueryFingerprint { get; set; } = string.Empty;

    /// <summary>Gets or sets the last check time.</summary>
    public DateTime LastCheckedUtc { get; set; }

    /// <summary>Gets or sets the outcome code (Identified, Finding, Skipped:LOCKED, ...).</summary>
    public string Outcome { get; set; } = string.Empty;
}

/// <summary>
/// A queued unit of work, keyed by item id.
/// </summary>
public sealed class QueueEntry
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the origin.</summary>
    public FindingOrigin Origin { get; set; }

    /// <summary>Gets or sets the audit run that enqueued it.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Gets or sets the first enqueue time.</summary>
    public DateTime EnqueuedUtc { get; set; }

    /// <summary>Gets or sets the time of the last event for this item (debounce anchor).</summary>
    public DateTime LastEventUtc { get; set; }

    /// <summary>Gets or sets the earliest processing time.</summary>
    public DateTime NotBeforeUtc { get; set; }

    /// <summary>Gets or sets the number of deferrals.</summary>
    public int Deferrals { get; set; }

    /// <summary>Gets or sets a value indicating whether the dedupe fingerprint is ignored.</summary>
    public bool Force { get; set; }
}

/// <summary>
/// One item in a bulk application run.
/// </summary>
public sealed class BatchItem
{
    /// <summary>Gets or sets the review id.</summary>
    public Guid ReviewId { get; set; }

    /// <summary>Gets or sets the candidate.</summary>
    public string CandidateId { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected finding version.</summary>
    public int ExpectedVersion { get; set; }

    /// <summary>Gets or sets the title for display.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the outcome (null while pending).</summary>
    public ApplyOutcome? Outcome { get; set; }

    /// <summary>Gets or sets the reason or error.</summary>
    public string? Message { get; set; }
}

/// <summary>
/// A committed bulk application run with per-item outcomes.
/// </summary>
public sealed class BatchRun
{
    /// <summary>Gets or sets the run id.</summary>
    public Guid RunId { get; set; }

    /// <summary>Gets or sets the plan id it executed.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Gets or sets the idempotency key supplied at commit.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the scope description shown at preview.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Gets or sets the actor.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Gets or sets the start time.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Gets or sets the end time.</summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public RunStatus Status { get; set; }

    /// <summary>Gets or sets the items.</summary>
    public List<BatchItem> Items { get; set; } = new();
}
