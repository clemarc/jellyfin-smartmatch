using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Configuration;
using Jellyfin.Plugin.SmartMatch.Discovery;
using Jellyfin.Plugin.SmartMatch.Library;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartMatch.Services;

/// <summary>
/// Server compatibility information. The plugin fails closed (no background work, no writes)
/// on a server ABI it was not built for.
/// </summary>
public interface IServerInfo
{
    /// <summary>Gets the server version.</summary>
    Version ServerVersion { get; }

    /// <summary>Gets a value indicating whether the server ABI is supported.</summary>
    bool IsSupported { get; }
}

/// <summary>
/// Request to apply one candidate.
/// </summary>
/// <param name="ReviewId">Finding.</param>
/// <param name="CandidateId">Candidate; null selects the recommended one.</param>
/// <param name="ExpectedVersion">Finding version the caller saw (ETag); null skips the check.</param>
/// <param name="Origin">Manual, Bulk or Automatic.</param>
/// <param name="Actor">Admin user id, or "automatic".</param>
/// <param name="BatchRunId">Batch run.</param>
public sealed record ApplyRequest(Guid ReviewId, string? CandidateId, int? ExpectedVersion, ApplyOrigin Origin, string Actor, Guid? BatchRunId = null);

/// <summary>
/// Result of an application attempt.
/// </summary>
/// <param name="Outcome">Outcome.</param>
/// <param name="Code">Machine-readable code.</param>
/// <param name="Message">Explanation.</param>
/// <param name="Finding">Updated finding.</param>
/// <param name="DecisionId">Decision record.</param>
public sealed record ApplyResult(ApplyOutcome Outcome, string Code, string Message, Finding? Finding, Guid? DecisionId);

/// <summary>
/// The single writer of Jellyfin metadata. Every path (apply one, selected, all eligible, automatic,
/// retry, rollback) goes through here: one commit at a time, a per-item lock, a fresh re-read and
/// full revalidation before any write, a checkpoint between the identity write and the refresh,
/// and verification against Jellyfin's actual item state afterwards.
/// </summary>
public sealed class MatchApplicationService : IDisposable
{
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private readonly ILibraryGateway _library;
    private readonly SmartMatchStore _store;
    private readonly IConfigProvider _config;
    private readonly ItemLockRegistry _locks;
    private readonly SelfWriteTracker _selfWrites;
    private readonly IServerInfo _server;
    private readonly TimeProvider _time;
    private readonly ILogger<MatchApplicationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MatchApplicationService"/> class.
    /// </summary>
    /// <param name="library">Library gateway.</param>
    /// <param name="store">Store.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="locks">Item locks.</param>
    /// <param name="selfWrites">Self-write tracker.</param>
    /// <param name="server">Server info.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public MatchApplicationService(
        ILibraryGateway library,
        SmartMatchStore store,
        IConfigProvider config,
        ItemLockRegistry locks,
        SelfWriteTracker selfWrites,
        IServerInfo server,
        TimeProvider time,
        ILogger<MatchApplicationService> logger)
    {
        _library = library;
        _store = store;
        _config = config;
        _locks = locks;
        _selfWrites = selfWrites;
        _server = server;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Applies a candidate.
    /// </summary>
    /// <param name="request">Request.</param>
    /// <param name="cancellationToken">Cancellation token (honoured before the write; the refresh has its own timeout).</param>
    /// <returns>Result.</returns>
    public async Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_server.IsSupported)
        {
            return new ApplyResult(ApplyOutcome.Failed, "UNSUPPORTED_SERVER", "This Jellyfin version is not supported; Smart Match does not write metadata.", null, null);
        }

        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var finding = _store.GetFinding(request.ReviewId);
            if (finding is null)
            {
                return new ApplyResult(ApplyOutcome.Failed, "NOT_FOUND", "Finding not found.", null, null);
            }

            using var itemLock = _locks.TryAcquire(finding.ItemId, "apply");
            if (itemLock is null)
            {
                return new ApplyResult(ApplyOutcome.Conflict, "BUSY", "Another operation is using this item; try again.", finding, null);
            }

            if (request.ExpectedVersion is { } expected && expected != finding.Version)
            {
                return new ApplyResult(ApplyOutcome.Conflict, "STALE_VERSION", "The finding changed since it was displayed; reload and review it again.", finding, null);
            }

            if (finding.Status == FindingStatus.Partial && request.Origin != ApplyOrigin.Automatic)
            {
                return await RetryRefreshLockedAsync(finding, request, cancellationToken).ConfigureAwait(false);
            }

            var gate = CheckStatus(finding, request);
            if (gate is not null)
            {
                return gate;
            }

            var candidateId = request.CandidateId ?? finding.RecommendedCandidateId;
            var candidate = finding.Candidates.Find(c => c.CandidateId == candidateId);
            if (candidate is null)
            {
                return new ApplyResult(ApplyOutcome.Failed, "CANDIDATE_NOT_FOUND", "The candidate does not belong to this finding.", finding, null);
            }

            if (!candidate.Applicable || candidate.ProviderIds.Count == 0)
            {
                return new ApplyResult(ApplyOutcome.Skipped, "NOT_APPLICABLE", "The candidate has no supported provider identifier.", finding, null);
            }

            if (request.Origin != ApplyOrigin.Manual && (candidate.CandidateId != finding.RecommendedCandidateId || candidate.YearConflict))
            {
                return new ApplyResult(ApplyOutcome.Skipped, "NOT_RECOMMENDED", "Bulk and automatic application only use the recommended candidate.", finding, null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var config = _config.Current;
            var item = _library.GetItem(finding.ItemId);
            var libraries = _library.GetLibraries();
            var conflict = Revalidate(finding, candidate, item, libraries, config);
            if (conflict is not null)
            {
                return conflict.Value.MarkConflict ? MarkConflict(finding, request, conflict.Value.Code, conflict.Value.Message) : new ApplyResult(conflict.Value.Outcome, conflict.Value.Code, conflict.Value.Message, finding, null);
            }

            if (request.Origin == ApplyOrigin.Automatic)
            {
                // Checked immediately before the commit: disabling auto-apply stops queued work here.
                var library = libraries.FirstOrDefault(l => l.Id == item!.LibraryId);
                if (!LibraryScope.AutoApplyEnabled(config, item!.LibraryId))
                {
                    return new ApplyResult(ApplyOutcome.Skipped, "AUTO_DISABLED", "Automatic application is disabled for this library.", finding, null);
                }

                if (!candidate.AutoEligible)
                {
                    return new ApplyResult(ApplyOutcome.Skipped, "NOT_AUTO_ELIGIBLE", "The candidate does not pass the automatic policy.", finding, null);
                }

                if (library?.SidecarRisk == true && !LibraryScope.SidecarAcknowledged(config, item.LibraryId))
                {
                    return new ApplyResult(ApplyOutcome.Skipped, "SIDECAR_BLOCKED", "This library saves metadata next to media; automatic application is blocked until an admin acknowledges it.", finding, null);
                }

                if (item.HasLocalNfo)
                {
                    return new ApplyResult(ApplyOutcome.Skipped, "LOCAL_NFO", "A local NFO file exists for this item; automatic application is blocked.", finding, null);
                }
            }

            return await CommitAsync(finding, candidate, item!, request, config, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <summary>
    /// Conditionally restores the plugin-owned fields of an applied finding.
    /// </summary>
    /// <param name="reviewId">Finding.</param>
    /// <param name="expectedVersion">Expected version.</param>
    /// <param name="actor">Admin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result.</returns>
    public async Task<ApplyResult> RollbackAsync(Guid reviewId, int? expectedVersion, string actor, CancellationToken cancellationToken)
    {
        if (!_server.IsSupported)
        {
            return new ApplyResult(ApplyOutcome.Failed, "UNSUPPORTED_SERVER", "This Jellyfin version is not supported.", null, null);
        }

        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var finding = _store.GetFinding(reviewId);
            if (finding is null)
            {
                return new ApplyResult(ApplyOutcome.Failed, "NOT_FOUND", "Finding not found.", null, null);
            }

            if (expectedVersion is { } v && v != finding.Version)
            {
                return new ApplyResult(ApplyOutcome.Conflict, "STALE_VERSION", "The finding changed since it was displayed.", finding, null);
            }

            if (finding.Status is not (FindingStatus.Applied or FindingStatus.Partial) || finding.LastDecisionId is null)
            {
                return new ApplyResult(ApplyOutcome.Skipped, "NOT_APPLIED", "Only applied findings can be rolled back.", finding, null);
            }

            var applied = _store.GetDecision(finding.LastDecisionId.Value);
            if (applied is null || applied.WrittenProviderIds.Count == 0)
            {
                return new ApplyResult(ApplyOutcome.Failed, "NO_DECISION", "The application record is no longer available.", finding, null);
            }

            using var itemLock = _locks.TryAcquire(finding.ItemId, "rollback");
            if (itemLock is null)
            {
                return new ApplyResult(ApplyOutcome.Conflict, "BUSY", "Another operation is using this item.", finding, null);
            }

            var item = _library.GetItem(finding.ItemId);
            if (item is null)
            {
                return new ApplyResult(ApplyOutcome.Conflict, "ITEM_MISSING", "The item no longer exists.", finding, null);
            }

            // Only restore when nobody changed the plugin-owned fields since the application.
            foreach (var (key, value) in applied.WrittenProviderIds)
            {
                if (!item.ProviderIds.TryGetValue(key, out var current) || !string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
                {
                    return new ApplyResult(ApplyOutcome.Conflict, "CHANGED_SINCE_APPLY", $"Provider id {key} was changed after Smart Match applied it; resolve manually in Jellyfin.", finding, null);
                }
            }

            string? restoreTitle = null;
            if (applied.WrittenTitle is not null)
            {
                var expectedTitle = applied.ObservedAfterTitle ?? applied.WrittenTitle;
                if (!string.Equals(item.Name, expectedTitle, StringComparison.Ordinal))
                {
                    return new ApplyResult(ApplyOutcome.Conflict, "CHANGED_SINCE_APPLY", "The title was changed after Smart Match applied it; resolve manually in Jellyfin.", finding, null);
                }

                restoreTitle = applied.BeforeTitle;
            }

            var restoreIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var removeKeys = new List<string>();
            foreach (var key in applied.WrittenProviderIds.Keys)
            {
                if (applied.BeforeProviderIds.TryGetValue(key, out var before) && !string.IsNullOrWhiteSpace(before))
                {
                    restoreIds[key] = before;
                }
                else
                {
                    removeKeys.Add(key);
                }
            }

            var decision = NewDecision(finding, item, "Rollback", null, actor, null);
            decision.BeforeProviderIds = new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase);
            decision.BeforeTitle = item.Name;
            decision.WrittenProviderIds = restoreIds;
            decision.WrittenTitle = restoreTitle;

            _selfWrites.Begin(item.Id);
            try
            {
                await _library.WriteIdentityAsync(item.Id, restoreIds, removeKeys, restoreTitle, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                decision.Outcome = ApplyOutcome.Failed;
                decision.ErrorCode = "WRITE_FAILED";
                decision.Message = ex.Message;
                RecordDecision(decision);
                return new ApplyResult(ApplyOutcome.Failed, "WRITE_FAILED", "Jellyfin rejected the rollback: " + ex.Message, finding, decision.DecisionId);
            }
            finally
            {
                _selfWrites.Complete(item.Id);
            }

            var after = _library.GetItem(item.Id);
            var verified = after is not null && removeKeys.All(k => !after.ProviderIds.TryGetValue(k, out var x) || string.IsNullOrWhiteSpace(x))
                && restoreIds.All(kv => after.ProviderIds.TryGetValue(kv.Key, out var x) && x == kv.Value);
            decision.ObservedAfterTitle = after?.Name;
            decision.ObservedAfterProviderIds = after is null ? new(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(after.ProviderIds, StringComparer.OrdinalIgnoreCase);
            decision.Outcome = verified ? ApplyOutcome.RolledBack : ApplyOutcome.Failed;
            decision.ErrorCode = verified ? null : "VERIFY_FAILED";
            decision.Message = verified
                ? "Provider ids" + (restoreTitle is null ? string.Empty : " and title") + " restored. Descriptions and artwork fetched by Jellyfin stay until a Jellyfin refresh replaces them."
                : "Rollback could not be verified.";
            RecordDecision(decision);

            _store.TryUpdateFinding(finding.ReviewId, null, f =>
            {
                f.Status = verified ? FindingStatus.RolledBack : f.Status;
                f.Reason = decision.Message;
                f.ReasonCode = verified ? "ROLLED_BACK" : "ROLLBACK_UNVERIFIED";
                f.LastDecisionId = decision.DecisionId;
                f.AppliedCandidateId = verified ? null : f.AppliedCandidateId;
                if (after is not null)
                {
                    f.ItemFingerprint = Fingerprints.Item(after);
                }

                return true;
            }, out var updated);
            await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Smart Match rollback for item {ItemId}: {Outcome}", item.Id, decision.Outcome);
            return new ApplyResult(decision.Outcome, verified ? "ROLLED_BACK" : "VERIFY_FAILED", decision.Message, updated, decision.DecisionId);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _commitGate.Dispose();

    private static ApplyResult? CheckStatus(Finding finding, ApplyRequest request)
    {
        switch (finding.Status)
        {
            case FindingStatus.Proposed:
                return null;
            case FindingStatus.NeedsReview when request.Origin == ApplyOrigin.Manual:
                return request.CandidateId is null
                    ? new ApplyResult(ApplyOutcome.Skipped, "CANDIDATE_REQUIRED", "This finding is ambiguous; choose a specific candidate.", finding, null)
                    : null;
            case FindingStatus.NeedsReview:
                return new ApplyResult(ApplyOutcome.Skipped, "AMBIGUOUS", "Ambiguous findings are never applied in bulk or automatically.", finding, null);
            case FindingStatus.Applied:
                return new ApplyResult(ApplyOutcome.Skipped, "ALREADY_APPLIED", "This finding was already applied.", finding, null);
            case FindingStatus.Ignored:
            case FindingStatus.Rejected:
                return new ApplyResult(ApplyOutcome.Skipped, finding.Status.ToString().ToUpperInvariant(), "The admin excluded this finding.", finding, null);
            case FindingStatus.Conflict:
                return new ApplyResult(ApplyOutcome.Conflict, "STALE_FINDING", "The item changed since analysis; use Retry to re-analyse before applying.", finding, null);
            default:
                return new ApplyResult(ApplyOutcome.Skipped, "NOT_APPLICABLE_STATUS", $"Findings in status {finding.Status} cannot be applied.", finding, null);
        }
    }

    private (ApplyOutcome Outcome, string Code, string Message, bool MarkConflict)? Revalidate(Finding finding, CandidateRecord candidate, ItemSnapshot? item, IReadOnlyList<LibraryInfo> libraries, PluginConfiguration config)
    {
        if (item is null)
        {
            return (ApplyOutcome.Conflict, "ITEM_MISSING", "The item no longer exists in Jellyfin.", true);
        }

        if (item.Kind != finding.MediaType)
        {
            return (ApplyOutcome.Conflict, "TYPE_CHANGED", "The item type changed.", true);
        }

        if (item.LibraryId != finding.LibraryId || !LibraryScope.InScope(libraries, config).Contains(item.LibraryId))
        {
            return (ApplyOutcome.Conflict, "LIBRARY_CHANGED", "The item is no longer in a selected library.", true);
        }

        if (Fingerprints.Path(_store.GetState().PathSalt, item.Path) != finding.PathHash)
        {
            return (ApplyOutcome.Conflict, "PATH_CHANGED", "The media path changed since analysis.", true);
        }

        if (item.IsLocked)
        {
            return (ApplyOutcome.Conflict, "LOCKED", "The item metadata is locked.", true);
        }

        var validator = new ProviderIdValidator(config.ExtraProviderIdKeys);
        if (validator.HasReliableIdentity(item.ProviderIds))
        {
            var same = candidate.ProviderIds.All(kv => item.ProviderIds.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase));
            return same
                ? (ApplyOutcome.Skipped, "ALREADY_IDENTIFIED", "The item already has this identity.", false)
                : (ApplyOutcome.Conflict, "CONFLICTING_ID", "A different provider identity appeared on the item since analysis.", true);
        }

        if (Fingerprints.Item(item) != finding.ItemFingerprint)
        {
            return (ApplyOutcome.Conflict, "ITEM_CHANGED", "The item changed since analysis; re-analyse before applying.", true);
        }

        if (_library.IsItemBusy(item.Id))
        {
            return (ApplyOutcome.Conflict, "BUSY", "Jellyfin is refreshing this item; try again shortly.", false);
        }

        return null;
    }

    private ApplyResult MarkConflict(Finding finding, ApplyRequest request, string code, string message)
    {
        _store.TryUpdateFinding(finding.ReviewId, null, f =>
        {
            f.Status = FindingStatus.Conflict;
            f.ReasonCode = code;
            f.Reason = message;
            return true;
        }, out var updated);
        var decision = NewDecision(finding, null, "Apply", request.Origin, request.Actor, request.BatchRunId);
        decision.CandidateId = request.CandidateId ?? finding.RecommendedCandidateId;
        decision.Outcome = ApplyOutcome.Conflict;
        decision.ErrorCode = code;
        decision.Message = message;
        RecordDecision(decision);
        _logger.LogInformation("Smart Match apply for item {ItemId} aborted: {Code}", finding.ItemId, code);
        return new ApplyResult(ApplyOutcome.Conflict, code, message, updated, decision.DecisionId);
    }

    private async Task<ApplyResult> CommitAsync(Finding finding, CandidateRecord candidate, ItemSnapshot item, ApplyRequest request, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var previousStatus = finding.Status;
        var decision = NewDecision(finding, item, "Apply", request.Origin, request.Actor, request.BatchRunId);
        decision.CandidateId = candidate.CandidateId;
        decision.BeforeProviderIds = new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase);
        decision.BeforeTitle = item.Name;

        var writeTitle = config.WriteCanonicalTitle
            && !item.NameLocked
            && string.Equals(item.Name, finding.OriginalName, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(candidate.Name)
            && !string.Equals(item.Name, candidate.Name, StringComparison.Ordinal);
        var newTitle = writeTitle ? candidate.Name : null;
        var ids = new Dictionary<string, string>(candidate.ProviderIds, StringComparer.OrdinalIgnoreCase);

        // Checkpoint before any write so a crash leaves an explainable state.
        decision.Outcome = ApplyOutcome.Failed;
        decision.ErrorCode = "IN_PROGRESS";
        RecordDecision(decision);
        _store.TryUpdateFinding(finding.ReviewId, null, f =>
        {
            f.Status = FindingStatus.Applying;
            f.LastDecisionId = decision.DecisionId;
            return true;
        }, out _);
        await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        _selfWrites.Begin(item.Id);
        try
        {
            try
            {
                await _library.WriteIdentityAsync(item.Id, ids, Array.Empty<string>(), newTitle, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                decision.ErrorCode = "WRITE_FAILED";
                decision.Message = "Jellyfin rejected the identity write: " + ex.Message;
                RecordDecision(decision);
                _store.TryUpdateFinding(finding.ReviewId, null, f =>
                {
                    f.Status = previousStatus;
                    f.ReasonCode = "WRITE_FAILED";
                    f.Reason = decision.Message;
                    return true;
                }, out var restored);
                await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogError(ex, "Smart Match could not write the identity of item {ItemId}", item.Id);
                return new ApplyResult(ApplyOutcome.Failed, "WRITE_FAILED", decision.Message, restored, decision.DecisionId);
            }

            decision.WrittenProviderIds = ids;
            decision.WrittenTitle = newTitle;
            decision.Outcome = ApplyOutcome.Partial;
            decision.ErrorCode = "REFRESH_PENDING";
            RecordDecision(decision);
            _store.TryUpdateFinding(finding.ReviewId, null, f =>
            {
                f.AppliedCandidateId = candidate.CandidateId;
                return true;
            }, out _);
            await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            return await RefreshAndVerifyAsync(finding.ReviewId, item.Id, candidate, decision, config).ConfigureAwait(false);
        }
        finally
        {
            _selfWrites.Complete(item.Id);
        }
    }

    private async Task<ApplyResult> RetryRefreshLockedAsync(Finding finding, ApplyRequest request, CancellationToken cancellationToken)
    {
        var previous = finding.LastDecisionId is null ? null : _store.GetDecision(finding.LastDecisionId.Value);
        var candidate = finding.Candidates.Find(c => c.CandidateId == (finding.AppliedCandidateId ?? previous?.CandidateId));
        if (previous is null || candidate is null || previous.WrittenProviderIds.Count == 0)
        {
            return new ApplyResult(ApplyOutcome.Failed, "NO_DECISION", "The partial application record is missing; use Retry to re-analyse.", finding, null);
        }

        if (request.CandidateId is not null && request.CandidateId != candidate.CandidateId)
        {
            return new ApplyResult(ApplyOutcome.Conflict, "PARTIAL_OTHER_CANDIDATE", "A different candidate was partially applied; roll it back first.", finding, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var item = _library.GetItem(finding.ItemId);
        if (item is null)
        {
            return MarkConflict(finding, request, "ITEM_MISSING", "The item no longer exists in Jellyfin.");
        }

        // Verified retry: the identity written earlier must still be in place.
        foreach (var (key, value) in previous.WrittenProviderIds)
        {
            if (!item.ProviderIds.TryGetValue(key, out var current) || !string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
            {
                return MarkConflict(finding, request, "CHANGED_SINCE_APPLY", "The provider identity changed after the partial application.");
            }
        }

        var decision = NewDecision(finding, item, "RetryRefresh", request.Origin, request.Actor, request.BatchRunId);
        decision.CandidateId = candidate.CandidateId;
        decision.BeforeProviderIds = previous.BeforeProviderIds;
        decision.BeforeTitle = previous.BeforeTitle;
        decision.WrittenProviderIds = previous.WrittenProviderIds;
        decision.WrittenTitle = previous.WrittenTitle;
        _selfWrites.Begin(item.Id);
        try
        {
            return await RefreshAndVerifyAsync(finding.ReviewId, item.Id, candidate, decision, _config.Current).ConfigureAwait(false);
        }
        finally
        {
            _selfWrites.Complete(item.Id);
        }
    }

    private async Task<ApplyResult> RefreshAndVerifyAsync(Guid reviewId, Guid itemId, CandidateRecord candidate, DecisionRecord decision, PluginConfiguration config)
    {
        string? refreshError = null;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(config.RefreshTimeoutSeconds)))
        {
            try
            {
                var request = new RefreshRequest(
                    config.RefreshProfile == RefreshProfile.ReplaceUnlockedText,
                    candidate.Name,
                    candidate.Year,
                    decision.WrittenProviderIds,
                    candidate.ProviderName);
                await _library.RefreshAsync(itemId, request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                refreshError = "Metadata refresh timed out.";
            }
            catch (Exception ex)
            {
                refreshError = "Metadata refresh failed: " + ex.Message;
                _logger.LogError(ex, "Smart Match refresh failed for item {ItemId}", itemId);
            }
        }

        var after = _library.GetItem(itemId);
        var idsPresent = after is not null && decision.WrittenProviderIds.All(kv => after.ProviderIds.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase));
        var nameOk = after is not null && !string.IsNullOrWhiteSpace(after.Name);
        var verified = refreshError is null && idsPresent && nameOk;

        decision.ObservedAfterTitle = after?.Name;
        decision.ObservedAfterProviderIds = after is null ? new(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(after.ProviderIds, StringComparer.OrdinalIgnoreCase);
        decision.Outcome = verified ? ApplyOutcome.Applied : ApplyOutcome.Partial;
        decision.RefreshOutcome = refreshError ?? (verified ? "Refreshed and verified." : "Refresh finished but the written identity was not found afterwards.");
        decision.ErrorCode = verified ? null : (refreshError is null ? "VERIFY_FAILED" : "REFRESH_FAILED");
        decision.Message = verified ? $"Applied {candidate.Name}." : decision.RefreshOutcome;
        decision.TimeUtc = _time.GetUtcNow().UtcDateTime;
        RecordDecision(decision);

        _store.TryUpdateFinding(reviewId, null, f =>
        {
            f.Status = verified ? FindingStatus.Applied : FindingStatus.Partial;
            f.ReasonCode = verified ? "APPLIED" : decision.ErrorCode;
            f.Reason = verified
                ? $"Applied by {decision.Origin?.ToString().ToLowerInvariant() ?? "manual"} action."
                : decision.RefreshOutcome + " Retry repeats only the refresh.";
            f.AppliedCandidateId = candidate.CandidateId;
            f.LastDecisionId = decision.DecisionId;
            if (after is not null)
            {
                f.ItemFingerprint = Fingerprints.Item(after);
            }

            return true;
        }, out var updated);

        if (after is not null)
        {
            _store.SetCoverage(itemId, new CoverageEntry
            {
                LibraryId = after.LibraryId,
                ItemFingerprint = Fingerprints.Item(after),
                LastCheckedUtc = _time.GetUtcNow().UtcDateTime,
                Outcome = verified ? "Applied" : "Partial",
            });
        }

        await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Smart Match applied candidate {Candidate} to item {ItemId}: {Outcome}", candidate.CandidateId, itemId, decision.Outcome);
        return new ApplyResult(decision.Outcome, verified ? "APPLIED" : decision.ErrorCode!, decision.Message!, updated, decision.DecisionId);
    }

    private DecisionRecord NewDecision(Finding finding, ItemSnapshot? item, string action, ApplyOrigin? origin, string actor, Guid? batchRunId) => new()
    {
        DecisionId = Guid.NewGuid(),
        ReviewId = finding.ReviewId,
        ItemId = finding.ItemId,
        ItemName = item?.Name ?? finding.OriginalName,
        Actor = actor,
        TimeUtc = _time.GetUtcNow().UtcDateTime,
        Action = action,
        Origin = origin,
        BatchRunId = batchRunId,
        ItemFingerprint = item is null ? finding.ItemFingerprint : Fingerprints.Item(item),
    };

    private void RecordDecision(DecisionRecord decision)
    {
        var config = _config.Current;
        _store.AddDecision(decision, config.RetentionDays, config.RetentionMaxDecisions);
    }
}
