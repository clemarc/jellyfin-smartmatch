using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Outcome of analysing one queued item.
/// </summary>
/// <param name="Code">Outcome code used in run counters.</param>
/// <param name="Finding">Finding written, if any.</param>
/// <param name="DeferUntilUtc">When set, the queue entry is kept and retried at this time.</param>
/// <param name="Searched">True when a remote search was made.</param>
public sealed record AnalysisOutcome(string Code, Finding? Finding, DateTime? DeferUntilUtc, bool Searched);

/// <summary>
/// Shared analysis pipeline for new scans, audits, reconciliation and manual retry:
/// eligibility, normalization, provider search, scoring and report persistence.
/// It never mutates Jellyfin metadata.
/// </summary>
public sealed class ItemAnalyzer
{
    /// <summary>Maximum consecutive provider failures before automatic retries stop.</summary>
    public const int MaxProviderRetries = 5;

    private readonly ILibraryGateway _library;
    private readonly IProviderSearchGateway _providers;
    private readonly ProviderSearchExecutor _executor;
    private readonly IMediaNameNormalizer _normalizer;
    private readonly SmartMatchStore _store;
    private readonly IConfigProvider _config;
    private readonly ItemLockRegistry _locks;
    private readonly TimeProvider _time;
    private readonly ILogger<ItemAnalyzer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemAnalyzer"/> class.
    /// </summary>
    /// <param name="library">Library gateway.</param>
    /// <param name="providers">Provider gateway.</param>
    /// <param name="executor">Search executor.</param>
    /// <param name="normalizer">Normalizer.</param>
    /// <param name="store">Store.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="locks">Item locks.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public ItemAnalyzer(
        ILibraryGateway library,
        IProviderSearchGateway providers,
        ProviderSearchExecutor executor,
        IMediaNameNormalizer normalizer,
        SmartMatchStore store,
        IConfigProvider config,
        ItemLockRegistry locks,
        TimeProvider time,
        ILogger<ItemAnalyzer> logger)
    {
        _library = library;
        _providers = providers;
        _executor = executor;
        _normalizer = normalizer;
        _store = store;
        _config = config;
        _locks = locks;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Analyses one item.
    /// </summary>
    /// <param name="entry">Queue entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Outcome.</returns>
    public async Task<AnalysisOutcome> AnalyzeAsync(QueueEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var now = _time.GetUtcNow().UtcDateTime;
        var config = _config.Current;

        using var itemLock = _locks.TryAcquire(entry.ItemId, "analysis");
        if (itemLock is null)
        {
            return new AnalysisOutcome("Deferred:APPLY_IN_PROGRESS", null, now.AddSeconds(30), false);
        }

        var item = _library.GetItem(entry.ItemId);
        var existing = _store.GetFindingByItem(entry.ItemId);
        if (item is null)
        {
            if (existing is not null && existing.Status is not (FindingStatus.Applied or FindingStatus.Partial))
            {
                _store.RemoveFinding(existing.ReviewId);
            }

            return new AnalysisOutcome("Skipped:MISSING", null, null, false);
        }

        var libraries = _library.GetLibraries();
        var library = libraries.FirstOrDefault(l => l.Id == item.LibraryId);
        var inScope = library is not null && LibraryScope.InScope(libraries, config).Contains(item.LibraryId);
        var validator = new ProviderIdValidator(config.ExtraProviderIdKeys);
        var eligibility = ItemEligibilityEvaluator.Evaluate(item, inScope, existing, validator, _library.IsItemBusy(item.Id));
        var itemFingerprint = Fingerprints.Item(item);

        if (eligibility.Kind == EligibilityKind.Defer)
        {
            var delay = TimeSpan.FromSeconds(Math.Max(config.SettleDelaySeconds, 10));
            return new AnalysisOutcome("Deferred:" + eligibility.Code, existing, now + delay, false);
        }

        if (eligibility.Kind == EligibilityKind.Skip)
        {
            RecordCoverage(item, itemFingerprint, string.Empty, "Skipped:" + eligibility.Code, now);
            if (existing is not null && eligibility.Code is "ALREADY_IDENTIFIED" or "LOCKED" or "NOT_IN_SCOPE"
                && existing.Status is FindingStatus.Proposed or FindingStatus.NeedsReview or FindingStatus.NoMatch or FindingStatus.ProviderError or FindingStatus.Conflict or FindingStatus.Queued)
            {
                _store.TryUpdateFinding(existing.ReviewId, null, f =>
                {
                    f.Status = FindingStatus.Resolved;
                    f.ReasonCode = eligibility.Code;
                    f.Reason = eligibility.Code == "ALREADY_IDENTIFIED" ? "Item was identified outside Smart Match." : eligibility.Message;
                    f.LastCheckedUtc = now;
                    f.ItemFingerprint = itemFingerprint;
                    return true;
                }, out var resolved);
                return new AnalysisOutcome("Skipped:" + eligibility.Code, resolved, null, false);
            }

            return new AnalysisOutcome("Skipped:" + eligibility.Code, existing, null, false);
        }

        if (config.DebugLogPaths)
        {
            // Opt-in only: normal logs identify items by id, never by path.
            _logger.LogDebug("Smart Match analysing item {ItemId} at {Path}", item.Id, item.Path);
        }

        var rules = ConfigurationValidator.BuildRuleSet(config);
        var (normalization, sourceName) = NameEvidence.Build(item, _normalizer, rules, () => _library.GetFallbackEpisodeFileName(item.Id));

        if (normalization.ProviderHints.Count > 0)
        {
            RecordCoverage(item, itemFingerprint, string.Empty, "Skipped:ID_MARKER", now);
            return new AnalysisOutcome("Skipped:ID_MARKER", existing, null, false);
        }

        var context = _providers.GetSearchContext(item.Id, item.Kind);
        var providerFingerprint = context?.Fingerprint ?? "none";
        var queryFingerprint = Fingerprints.Query(normalization.SearchTitle, normalization.TrustedYear, rules.Revision, providerFingerprint);
        var coverage = _store.GetCoverage(item.Id);

        // At most one provider search per unchanged item/query/provider configuration.
        if (!entry.Force && existing is not null && coverage is not null
            && coverage.ItemFingerprint == itemFingerprint && coverage.QueryFingerprint == queryFingerprint
            && existing.Status is FindingStatus.Proposed or FindingStatus.NeedsReview or FindingStatus.NoMatch or FindingStatus.Conflict)
        {
            return new AnalysisOutcome("Unchanged", existing, null, false);
        }

        if (!entry.Force && existing is { Status: FindingStatus.ProviderError, NextRetryUtc: { } retryAt } && retryAt > now
            && existing.QueryFingerprint == queryFingerprint)
        {
            return new AnalysisOutcome("Deferred:PROVIDER_BACKOFF", existing, retryAt, false);
        }

        var finding = existing ?? new Finding { FirstSeenUtc = now };
        finding.ItemId = item.Id;
        finding.LibraryId = item.LibraryId;
        finding.LibraryName = item.LibraryName;
        finding.MediaType = item.Kind;
        finding.OriginalName = item.Name;
        finding.SourceName = sourceName;
        finding.PathHash = Fingerprints.Path(_store.GetState().PathSalt, item.Path);
        finding.SearchTitle = normalization.SearchTitle;
        finding.ComparisonKey = normalization.ComparisonKey;
        finding.TrustedYear = normalization.TrustedYear;
        finding.EpisodeContext = normalization.EpisodeContext;
        finding.SourceKind = normalization.Source;
        finding.NormalizationTrace = normalization.Steps.Select(s => s.ToString()).ToList();
        finding.Warnings = normalization.Warnings.ToList();
        finding.Origin = existing?.Origin is { } o && entry.Origin != FindingOrigin.Manual ? o : entry.Origin;
        finding.ItemFingerprint = itemFingerprint;
        finding.QueryFingerprint = queryFingerprint;
        finding.LastCheckedUtc = now;
        finding.SidecarRisk = library?.SidecarRisk ?? false;
        finding.AppliedCandidateId = null;
        finding.AdminNote = existing?.Status == FindingStatus.Rejected ? existing.AdminNote : null;

        if (!normalization.IsSafe)
        {
            SetResult(finding, FindingStatus.NoMatch, "UNPARSABLE", "The name could not be cleaned into a usable title.", new List<CandidateRecord>(), null, new List<string> { "unparsable name" });
            return Save(item, finding, itemFingerprint, queryFingerprint, "NoMatch", now, false);
        }

        if (context is null || context.Providers.Count == 0)
        {
            SetResult(finding, FindingStatus.ProviderError, "NO_PROVIDERS", "No remote metadata providers are enabled for this library and media type.", new List<CandidateRecord>(), null, new List<string> { "no providers" });
            finding.ProviderErrors = new List<string> { "No enabled remote search providers." };
            finding.NextRetryUtc = null;
            return Save(item, finding, itemFingerprint, queryFingerprint, "ProviderError", now, false);
        }

        var budget = new SearchBudget(
            TimeSpan.FromMilliseconds(config.MinSearchIntervalMs),
            TimeSpan.FromSeconds(config.ProviderTimeoutSeconds),
            config.ProviderMaxAttempts);
        var outcome = await _executor.SearchAsync(context, normalization.SearchTitle, normalization.TrustedYear, budget, cancellationToken).ConfigureAwait(false);
        finding.ProviderErrors = outcome.Failures.Select(f => $"{f.Provider}: {f.Code} {f.Message}").ToList();

        if (outcome.AllFailed)
        {
            finding.ProviderFailures = (existing?.Status == FindingStatus.ProviderError ? existing.ProviderFailures : 0) + 1;
            var transient = outcome.Failures.Any(f => f.Transient);
            DateTime? nextRetry = transient && finding.ProviderFailures < MaxProviderRetries
                ? now + TimeSpan.FromMinutes(Math.Min(15 * Math.Pow(2, finding.ProviderFailures - 1), 24 * 60))
                : null;
            finding.NextRetryUtc = nextRetry;
            var code = outcome.Failures.Select(f => f.Code).Distinct().Count() == 1 ? outcome.Failures[0].Code : "PROVIDER_UNAVAILABLE";
            SetResult(
                finding,
                FindingStatus.ProviderError,
                code,
                nextRetry is null ? "Providers are unavailable; retry manually." : string.Create(CultureInfo.InvariantCulture, $"Providers are unavailable; automatic retry after {nextRetry:u}."),
                existing?.Candidates ?? new List<CandidateRecord>(),
                null,
                new List<string> { "provider unavailable" });
            var saved = Save(item, finding, itemFingerprint, queryFingerprint, "ProviderError", now, true);
            return saved with { DeferUntilUtc = nextRetry };
        }

        finding.ProviderFailures = 0;
        finding.NextRetryUtc = null;
        var ranker = new MatchCandidateRanker(validator, ConfigurationValidator.BuildMatchSettings(config));
        var classification = ranker.Classify(item.Kind, normalization, outcome.Results, outcome.Failures.Count > 0);
        SetResult(finding, classification.Status, classification.Status.ToString().ToUpperInvariant(), classification.Reason, classification.Candidates, classification.RecommendedCandidateId, classification.AutoBlockers);
        _logger.LogInformation(
            "Smart Match analysed item {ItemId}: {Status} with {Count} candidate(s)",
            item.Id,
            classification.Status,
            classification.Candidates.Count);
        return Save(item, finding, itemFingerprint, queryFingerprint, classification.Status.ToString(), now, true);
    }

    /// <summary>
    /// Re-ranks open findings classified by an older <see cref="MatchCandidateRanker.Revision"/> from
    /// their saved candidates. No provider is searched, nothing is written to Jellyfin and nothing is
    /// applied automatically; reviewed, applied, ignored and rejected findings are left alone.
    /// </summary>
    /// <returns>Number of findings re-ranked.</returns>
    public int RerankStoredFindings()
    {
        var config = _config.Current;
        var ranker = new MatchCandidateRanker(new ProviderIdValidator(config.ExtraProviderIdKeys), ConfigurationValidator.BuildMatchSettings(config));
        var stale = _store.QueryFindings(f => f.RankerRevision < MatchCandidateRanker.Revision
            && f.Status is FindingStatus.Proposed or FindingStatus.NeedsReview
            && f.Candidates.Count > 0);
        var count = 0;
        foreach (var f in stale)
        {
            using var itemLock = _locks.TryAcquire(f.ItemId, "rerank");
            if (itemLock is null)
            {
                continue;
            }

            // Only safe names reach a search, so every finding with candidates had a safe normalization.
            var query = new NormalizationResult(
                f.SourceName,
                f.SearchTitle,
                f.ComparisonKey,
                f.TrustedYear,
                f.EpisodeContext,
                new Dictionary<string, string>(),
                Array.Empty<NormalizationStep>(),
                f.SourceKind,
                true,
                f.Warnings);
            var classification = ranker.Reclassify(f.MediaType, query, f.Candidates, f.ProviderErrors.Count > 0);
            var result = _store.TryUpdateFinding(f.ReviewId, f.Version, x =>
            {
                SetResult(x, classification.Status, classification.Status.ToString().ToUpperInvariant(), classification.Reason, classification.Candidates, classification.RecommendedCandidateId, classification.AutoBlockers);
                return true;
            }, out _);
            if (result == UpdateResult.Updated)
            {
                count++;
            }
        }

        if (count > 0)
        {
            _logger.LogInformation("Smart Match re-ranked {Count} finding(s) from saved candidates after a ranking update", count);
        }

        return count;
    }

    private static void SetResult(Finding finding, FindingStatus status, string code, string reason, List<CandidateRecord> candidates, string? recommended, List<string> blockers)
    {
        finding.RankerRevision = MatchCandidateRanker.Revision;
        finding.Status = status;
        finding.ReasonCode = code;
        finding.Reason = reason;
        finding.Candidates = candidates;
        finding.RecommendedCandidateId = recommended;
        finding.AutoBlockers = blockers;
    }

    private AnalysisOutcome Save(ItemSnapshot item, Finding finding, string itemFingerprint, string queryFingerprint, string code, DateTime now, bool searched)
    {
        var stored = _store.UpsertFinding(finding);
        RecordCoverage(item, itemFingerprint, queryFingerprint, "Finding:" + code, now);
        return new AnalysisOutcome(code, stored, null, searched);
    }

    private void RecordCoverage(ItemSnapshot item, string itemFingerprint, string queryFingerprint, string outcome, DateTime now)
    {
        _store.SetCoverage(item.Id, new CoverageEntry
        {
            LibraryId = item.LibraryId,
            ItemFingerprint = itemFingerprint,
            QueryFingerprint = queryFingerprint,
            LastCheckedUtc = now,
            Outcome = outcome,
        });
    }
}
