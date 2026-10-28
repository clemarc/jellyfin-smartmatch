using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Normalization;

namespace Jellyfin.Plugin.SmartMatch.Matching;

/// <summary>
/// A raw search result from one provider.
/// </summary>
/// <param name="Name">Result name.</param>
/// <param name="Year">Production year.</param>
/// <param name="ProviderIds">Identifiers reported by the provider.</param>
/// <param name="ProviderName">Provider that returned the result.</param>
public sealed record ProviderCandidate(string Name, int? Year, IReadOnlyDictionary<string, string> ProviderIds, string ProviderName);

/// <summary>
/// Thresholds for ranking and policy. Defaults follow TECHNICAL_DESIGN.md §7.
/// </summary>
public sealed record MatchSettings
{
    /// <summary>Gets the accepted year difference for partial year credit.</summary>
    public int YearTolerance { get; init; } = 1;

    /// <summary>Gets the minimum score for a finding to be Proposed (bulk-eligible).</summary>
    public int ProposeMinScore { get; init; } = 70;

    /// <summary>Gets the minimum title-similarity component for a Proposed finding.</summary>
    public int ProposeMinNameScore { get; init; } = 60;

    /// <summary>Gets the minimum lead over the runner-up for a Proposed finding.</summary>
    public int ProposeMargin { get; init; } = 10;

    /// <summary>Gets the minimum score for automatic application.</summary>
    public int AutoMinScore { get; init; } = 95;

    /// <summary>Gets the minimum lead over the runner-up for automatic application.</summary>
    public int AutoMargin { get; init; } = 10;

    /// <summary>Gets a value indicating whether automation needs a trusted year.</summary>
    public bool AutoRequireYear { get; init; } = true;
}

/// <summary>
/// Classification of one search.
/// </summary>
/// <param name="Status">Proposed, NeedsReview or NoMatch.</param>
/// <param name="Candidates">Ranked candidates, best first, at most 10.</param>
/// <param name="RecommendedCandidateId">Best applicable candidate.</param>
/// <param name="Reason">Explanation of the classification.</param>
/// <param name="AutoBlockers">Reasons the recommendation fails the automatic gate (empty when it passes).</param>
public sealed record MatchClassification(
    FindingStatus Status,
    List<CandidateRecord> Candidates,
    string? RecommendedCandidateId,
    string Reason,
    List<string> AutoBlockers);

/// <summary>
/// Deterministic ranking (display score) and the separate hard gates for manual proposal and automation.
/// </summary>
public sealed class MatchCandidateRanker
{
    /// <summary>Maximum candidates stored per finding.</summary>
    public const int MaxCandidates = 10;

    /// <summary>
    /// Revision of the ranking and policy logic. Bump it whenever a change could classify the same
    /// provider results differently; stored findings from an older revision are then re-ranked from
    /// their saved candidates at startup, without new provider searches.
    /// </summary>
    public const int Revision = 2;

    private const string StoredCandidateKey = "SmartMatchStoredCandidate";

    private readonly ProviderIdValidator _validator;
    private readonly MatchSettings _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="MatchCandidateRanker"/> class.
    /// </summary>
    /// <param name="validator">Provider id validator.</param>
    /// <param name="settings">Thresholds.</param>
    public MatchCandidateRanker(ProviderIdValidator validator, MatchSettings settings)
    {
        _validator = validator;
        _settings = settings;
    }

    /// <summary>
    /// Deduplicates, scores and classifies provider results.
    /// </summary>
    /// <param name="kind">Media kind.</param>
    /// <param name="query">Normalization of the item name.</param>
    /// <param name="results">Raw results from all providers.</param>
    /// <param name="anyProviderFailed">True when at least one provider failed (blocks automation).</param>
    /// <returns>The classification.</returns>
    public MatchClassification Classify(MediaKind kind, NormalizationResult query, IReadOnlyList<ProviderCandidate> results, bool anyProviderFailed)
    {
        var candidates = Score(kind, query, Deduplicate(kind, results));
        var autoBlockers = new List<string>();

        if (candidates.Count == 0)
        {
            return new MatchClassification(FindingStatus.NoMatch, candidates, null, "Providers returned no candidates.", new List<string> { "no candidates" });
        }

        var credible = candidates.Where(c => !c.YearConflict).ToList();
        var top = credible.FirstOrDefault(c => c.Applicable);
        if (top is null)
        {
            var why = credible.Count == 0
                ? "Every candidate conflicts with the year in the name."
                : "No candidate has a supported provider identifier.";
            return new MatchClassification(FindingStatus.NeedsReview, candidates, null, why, new List<string> { why });
        }

        var others = credible.Where(c => c.CandidateId != top.CandidateId).ToList();
        var runnerUp = others.FirstOrDefault();
        var margin = top.RankScore - (runnerUp?.RankScore ?? 0);
        var rival = others.FirstOrDefault(o => IsNearEquivalent(top, o));
        var catalogueAmbiguous = AmbiguityCatalogue.Contains(query.ComparisonKey) && query.TrustedYear is null;
        var nameScore = top.TitleScore;

        var reviewReasons = new List<string>();
        if (!query.IsSafe)
        {
            reviewReasons.Add("name could not be cleaned safely");
        }

        if (rival is not null)
        {
            reviewReasons.Add($"near-equivalent rival: {Describe(rival)}");
        }

        if (catalogueAmbiguous)
        {
            reviewReasons.Add("title is known to have several productions and no year was found");
        }

        if (top.RankScore < _settings.ProposeMinScore)
        {
            reviewReasons.Add(string.Create(CultureInfo.InvariantCulture, $"score {top.RankScore} below {_settings.ProposeMinScore}"));
        }

        if (nameScore < _settings.ProposeMinNameScore)
        {
            reviewReasons.Add("title similarity too low");
        }

        if (runnerUp is not null && margin < _settings.ProposeMargin)
        {
            reviewReasons.Add(string.Create(CultureInfo.InvariantCulture, $"lead over runner-up is {margin} (< {_settings.ProposeMargin})"));
        }

        // Automation gate: stricter, every condition mandatory.
        if (reviewReasons.Count > 0)
        {
            autoBlockers.AddRange(reviewReasons);
        }

        if (!top.TitleExact)
        {
            autoBlockers.Add("title is not an exact match");
        }

        var yearKnown = query.TrustedYear is not null;
        if (yearKnown && top.Year != query.TrustedYear)
        {
            autoBlockers.Add("year is not an exact match");
        }

        if (!yearKnown && (_settings.AutoRequireYear || AmbiguityCatalogue.Contains(query.ComparisonKey)))
        {
            autoBlockers.Add("no trusted year in the name");
        }

        if (!yearKnown && candidates.Count > 1)
        {
            autoBlockers.Add("more than one candidate and no year to separate them");
        }

        var requiredScore = yearKnown ? _settings.AutoMinScore : _settings.AutoMinScore - 20;
        if (top.RankScore < requiredScore)
        {
            autoBlockers.Add(string.Create(CultureInfo.InvariantCulture, $"score {top.RankScore} below automatic threshold {requiredScore}"));
        }

        var anyRunnerUp = candidates.FirstOrDefault(c => c.CandidateId != top.CandidateId);
        if (anyRunnerUp is not null && top.RankScore - anyRunnerUp.RankScore < _settings.AutoMargin)
        {
            autoBlockers.Add("runner-up too close for automation");
        }

        if (query.Warnings.Count > 0)
        {
            autoBlockers.Add("name cleaning produced warnings: " + string.Join("; ", query.Warnings));
        }

        if (anyProviderFailed)
        {
            autoBlockers.Add("at least one enabled provider failed during the search");
        }

        top.AutoEligible = autoBlockers.Count == 0;

        if (reviewReasons.Count > 0)
        {
            return new MatchClassification(FindingStatus.NeedsReview, candidates, top.CandidateId, "Needs review: " + string.Join("; ", reviewReasons) + ".", Distinct(autoBlockers));
        }

        var reason = top.AutoEligible
            ? "High-confidence unique match."
            : "Clear best candidate; apply manually (" + string.Join("; ", Distinct(autoBlockers)) + ").";
        return new MatchClassification(FindingStatus.Proposed, candidates, top.CandidateId, reason, Distinct(autoBlockers));
    }

    /// <summary>
    /// Classifies a finding again from the candidates saved with it, without searching providers.
    /// Candidates that were one identity stay one identity: each provider's copy carries the stored
    /// candidate id as a non-writable marker, so the id-based merge regroups them.
    /// </summary>
    /// <param name="kind">Media kind.</param>
    /// <param name="query">Normalization rebuilt from the finding.</param>
    /// <param name="stored">Candidates saved with the finding.</param>
    /// <param name="anyProviderFailed">True when a provider failed during the original search.</param>
    /// <returns>The classification.</returns>
    public MatchClassification Reclassify(MediaKind kind, NormalizationResult query, IReadOnlyList<CandidateRecord> stored, bool anyProviderFailed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var results = new List<ProviderCandidate>();
        foreach (var c in stored)
        {
            var ids = new Dictionary<string, string>(c.ProviderIds, StringComparer.OrdinalIgnoreCase) { [StoredCandidateKey] = c.CandidateId };
            var providers = c.Providers.Count > 0 ? c.Providers : new List<string> { c.ProviderName };
            results.AddRange(providers.Select(p => new ProviderCandidate(c.Name, c.Year, ids, p)));
        }

        return Classify(kind, query, results, anyProviderFailed);
    }

    private static List<string> Distinct(List<string> items) => items.Distinct(StringComparer.Ordinal).ToList();

    private static string Describe(CandidateRecord c)
        => c.Year is null ? c.Name : string.Create(CultureInfo.InvariantCulture, $"{c.Name} ({c.Year})");

    private bool IsNearEquivalent(CandidateRecord top, CandidateRecord other)
    {
        if (MediaNameNormalizer.ComparisonKey(top.Name) != MediaNameNormalizer.ComparisonKey(other.Name))
        {
            return false;
        }

        if (top.Year is null || other.Year is null)
        {
            return true;
        }

        return Math.Abs(top.Year.Value - other.Year.Value) <= _settings.YearTolerance;
    }

    private List<MergedResult> Deduplicate(MediaKind kind, IReadOnlyList<ProviderCandidate> results)
    {
        var merged = new List<MergedResult>();
        foreach (var r in results)
        {
            if (string.IsNullOrWhiteSpace(r.Name))
            {
                continue;
            }

            var writable = _validator.FilterWritable(kind, r.ProviderIds);
            var allIds = new Dictionary<string, string>(r.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            var existing = merged.Find(m => SharesId(m.AllIds, allIds));
            if (existing is null)
            {
                merged.Add(new MergedResult(r, writable, allIds));
                continue;
            }

            existing.Absorb(writable, allIds, new[] { r.ProviderName });
        }

        MergeSameWorkAcrossProviders(merged);
        return merged;

        static bool SharesId(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            foreach (var (k, v) in a)
            {
                if (!string.IsNullOrWhiteSpace(v) && b.TryGetValue(k, out var other) && string.Equals(v.Trim(), other?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Providers search different catalogues, so one work often comes back with disjoint ids, e.g. a
    /// Tmdb id from TheMovieDb and an Imdb id from OMDb. Those results are merged when the title key
    /// and year are identical, no id key carries two values, and no provider returned two results
    /// with that title and year (which would mean genuinely different productions).
    /// </summary>
    private static void MergeSameWorkAcrossProviders(List<MergedResult> merged)
    {
        var groups = merged
            .Where(m => m.First.Year is not null)
            .GroupBy(m => (Key: MediaNameNormalizer.ComparisonKey(m.First.Name), Year: m.First.Year!.Value))
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();

        foreach (var group in groups)
        {
            var providers = group.SelectMany(m => m.Providers).ToList();
            if (providers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != providers.Count)
            {
                continue;
            }

            var conflict = group
                .SelectMany(m => m.AllIds.Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && kv.Key != StoredCandidateKey))
                .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Select(kv => kv.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
            if (conflict)
            {
                continue;
            }

            var target = group[0];
            foreach (var other in group.Skip(1))
            {
                target.Absorb(other.Writable, other.AllIds, other.Providers);
                merged.Remove(other);
            }

            target.MergedByTitleAndYear = true;
        }
    }

    private List<CandidateRecord> Score(MediaKind kind, NormalizationResult query, List<MergedResult> merged)
    {
        var list = new List<CandidateRecord>();
        foreach (var m in merged)
        {
            var (first, writable, providers) = (m.First, m.Writable, m.Providers);
            var reasons = new List<string>();
            var candidateKey = MediaNameNormalizer.ComparisonKey(first.Name);
            var similarity = TitleSimilarity.Compute(query.ComparisonKey, candidateKey);
            var exact = similarity >= 1.0;
            int nameScore;
            if (exact)
            {
                nameScore = 70;
                reasons.Add("+70 title matches exactly");
            }
            else if (similarity >= 0.5)
            {
                nameScore = (int)Math.Round(69 * similarity, MidpointRounding.AwayFromZero);
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"+{nameScore} title similarity {similarity:0.00}"));
            }
            else
            {
                nameScore = (int)Math.Round(40 * similarity, MidpointRounding.AwayFromZero);
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"+{nameScore} weak title similarity {similarity:0.00}"));
            }

            var yearScore = 0;
            var yearConflict = false;
            if (query.TrustedYear is { } qy && first.Year is { } cy)
            {
                var delta = Math.Abs(qy - cy);
                if (delta == 0)
                {
                    yearScore = 20;
                    reasons.Add("+20 year matches");
                }
                else if (delta <= _settings.YearTolerance)
                {
                    yearScore = 10;
                    reasons.Add(string.Create(CultureInfo.InvariantCulture, $"+10 year within {delta}"));
                }
                else
                {
                    yearConflict = true;
                    reasons.Add(string.Create(CultureInfo.InvariantCulture, $"+0 year conflict ({cy} vs {qy})"));
                }
            }
            else if (query.TrustedYear is null)
            {
                reasons.Add("+0 no trusted year in the name");
            }
            else
            {
                reasons.Add("+0 provider gave no year");
            }

            var corroboration = providers.Count >= 2 ? 5 : 0;
            if (corroboration > 0)
            {
                reasons.Add("+5 returned by " + string.Join(" and ", providers));
            }

            if (m.MergedByTitleAndYear)
            {
                reasons.Add("same title and year from each provider; their ids are combined");
            }

            var parse = query.IsSafe && query.Warnings.Count == 0 ? 5 : 0;
            if (parse > 0)
            {
                reasons.Add("+5 clean parse");
            }
            else
            {
                reasons.Add("+0 parse has warnings");
            }

            var applicable = writable.Count > 0;
            if (!applicable)
            {
                reasons.Add("no supported provider id; review only");
            }

            list.Add(new CandidateRecord
            {
                CandidateId = _validator.CandidateId(kind, writable, first.Name, first.Year),
                Name = first.Name.Trim(),
                Year = first.Year,
                ProviderName = first.ProviderName,
                Providers = providers,
                ProviderIds = writable,
                TitleScore = nameScore,
                RankScore = yearConflict ? Math.Min(nameScore, 40) : nameScore + yearScore + corroboration + parse,
                Reasons = reasons,
                TitleExact = exact,
                YearConflict = yearConflict,
                Applicable = applicable,
            });
        }

        // Disambiguate identical candidate ids (e.g. two id-less results with the same name).
        foreach (var group in list.GroupBy(c => c.CandidateId).Where(g => g.Count() > 1))
        {
            var n = 0;
            foreach (var c in group.Skip(1))
            {
                c.CandidateId += "#" + (++n).ToString(CultureInfo.InvariantCulture);
            }
        }

        return list
            .OrderByDescending(c => c.RankScore)
            .ThenBy(c => c.YearConflict)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToList();
    }

    private sealed class MergedResult
    {
        public MergedResult(ProviderCandidate first, Dictionary<string, string> writable, Dictionary<string, string> allIds)
        {
            First = first;
            Writable = writable;
            AllIds = allIds;
            Providers = new List<string> { first.ProviderName };
        }

        public ProviderCandidate First { get; }

        public Dictionary<string, string> Writable { get; }

        public Dictionary<string, string> AllIds { get; }

        public List<string> Providers { get; }

        public bool MergedByTitleAndYear { get; set; }

        public void Absorb(IReadOnlyDictionary<string, string> writable, IReadOnlyDictionary<string, string> allIds, IEnumerable<string> providers)
        {
            foreach (var (k, v) in writable)
            {
                Writable.TryAdd(k, v);
            }

            foreach (var (k, v) in allIds)
            {
                AllIds.TryAdd(k, v);
            }

            foreach (var p in providers)
            {
                if (!Providers.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    Providers.Add(p);
                }
            }
        }
    }
}

/// <summary>
/// Small, explicit catalogue of titles with several well-known productions. It is one input to the
/// policy, never the only safety check: rival detection applies to every title.
/// </summary>
public static class AmbiguityCatalogue
{
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "the office", "doctor who", "shameless", "house of cards", "battlestar galactica", "the twilight zone",
        "queer as folk", "skins", "being human", "life on mars", "the killing", "dune", "it", "halloween",
        "the outer limits", "dallas", "charmed", "hawaii five 0", "the bridge", "the night of", "teen wolf",
        "fargo", "westworld", "lost in space", "v", "the prisoner", "ghosts", "the tourist", "dark",
    };

    /// <summary>
    /// Returns true when the comparison key is in the catalogue.
    /// </summary>
    /// <param name="comparisonKey">Comparison key.</param>
    /// <returns>True when known-ambiguous.</returns>
    public static bool Contains(string comparisonKey) => Keys.Contains(comparisonKey);
}
