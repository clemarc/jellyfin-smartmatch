using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Library;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Normalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace SmartMatch.UnitTests;

public class MatchingTests
{
    private static readonly MediaNameNormalizer Normalizer = new(() => 2028);
    private static readonly MatchCandidateRanker Ranker = new(new ProviderIdValidator(), new MatchSettings());

    private static NormalizationResult Query(string name, MediaKind kind = MediaKind.Movie, NameSourceKind source = NameSourceKind.MovieFolder)
        => Normalizer.Normalize(new MediaNameInput(name, source, kind), RuleSet.Default);

    private static ProviderCandidate C(string name, int? year, string? tmdb, string provider = "TheMovieDb", string? imdb = null)
    {
        var ids = new Dictionary<string, string>();
        if (tmdb is not null)
        {
            ids["Tmdb"] = tmdb;
        }

        if (imdb is not null)
        {
            ids["Imdb"] = imdb;
        }

        return new ProviderCandidate(name, year, ids, provider);
    }

    [Fact]
    public void Exact_title_and_year_is_proposed_and_auto_eligible()
    {
        var result = Ranker.Classify(MediaKind.Movie, Query("The Dark Knight (2008)"), new[] { C("The Dark Knight", 2008, "155"), C("The Dark Knight Rises", 2012, "49026") }, false);
        Assert.Equal(FindingStatus.Proposed, result.Status);
        var top = result.Candidates[0];
        Assert.Equal("tmdb:155", top.CandidateId);
        Assert.Equal(95, top.RankScore);
        Assert.True(top.AutoEligible);
        Assert.Empty(result.AutoBlockers);
    }

    [Fact]
    public void Same_work_from_two_providers_with_disjoint_ids_is_one_candidate()
    {
        var q = Query("www.UIndex.org - Chicago.Fire.S14E08.1080p.WEB.h264-ETHEL", MediaKind.Series, NameSourceKind.SeriesFolder);
        var result = Ranker.Classify(
            MediaKind.Series,
            q,
            new[]
            {
                C("Chicago Fire", 2012, "44006"),
                C("Chicago Fire", 2012, null, "The Open Movie Database", "tt2261391"),
                C("Chicago Fire FC", 1998, null, "The Open Movie Database", "tt40309254"),
                C("Chicago Fire: I Am a Firefighter", 2014, null, "The Open Movie Database", "tt4120900"),
            },
            false);
        Assert.Equal(FindingStatus.Proposed, result.Status);
        Assert.Equal(3, result.Candidates.Count);
        var top = result.Candidates[0];
        Assert.Equal("tmdb:44006", result.RecommendedCandidateId);
        Assert.Equal("tt2261391", top.ProviderIds["Imdb"]);
        Assert.Equal(80, top.RankScore);
        Assert.Contains(top.Reasons, r => r.StartsWith("same title and year", StringComparison.Ordinal));
        Assert.False(top.AutoEligible);
        Assert.DoesNotContain(result.AutoBlockers, b => b.Contains("rival", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_works_with_one_title_and_year_from_one_provider_are_never_merged()
    {
        var result = Ranker.Classify(
            MediaKind.Series,
            Query("Ghosts", MediaKind.Series, NameSourceKind.SeriesFolder),
            new[]
            {
                C("Ghosts", 2019, "100"),
                C("Ghosts", 2019, "200"),
                C("Ghosts", 2019, null, "The Open Movie Database", "tt8594324"),
            },
            false);
        Assert.Equal(FindingStatus.NeedsReview, result.Status);
        Assert.Equal(3, result.Candidates.Count);
    }

    [Fact]
    public void Title_and_year_merge_needs_agreeing_ids_and_identical_years()
    {
        var result = Ranker.Classify(
            MediaKind.Movie,
            Query("Heat"),
            new[]
            {
                C("Heat", 1995, "949"),
                C("Heat", 1995, "999", "The Open Movie Database", "tt0113277"),
                C("Heat", 1986, null, "The Open Movie Database", "tt0093164"),
            },
            false);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal(FindingStatus.NeedsReview, result.Status);
    }

    [Fact]
    public void Reclassify_from_stored_candidates_merges_without_new_results()
    {
        var q = Query("www.UIndex.org - Chicago.Fire.S14E08.1080p.WEB.h264-ETHEL", MediaKind.Series, NameSourceKind.SeriesFolder);
        var stored = new List<CandidateRecord>
        {
            new() { CandidateId = "tmdb:44006", Name = "Chicago Fire", Year = 2012, ProviderName = "TheMovieDb", Providers = new() { "TheMovieDb" }, ProviderIds = new() { ["Tmdb"] = "44006" } },
            new() { CandidateId = "imdb:tt2261391", Name = "Chicago Fire", Year = 2012, ProviderName = "The Open Movie Database", Providers = new() { "The Open Movie Database" }, ProviderIds = new() { ["Imdb"] = "tt2261391" } },
            new() { CandidateId = "tmdb:1", Name = "Chicago Fire FC", Year = 1998, ProviderName = "TheMovieDb", Providers = new() { "TheMovieDb", "The Open Movie Database" }, ProviderIds = new() { ["Tmdb"] = "1", ["Imdb"] = "tt40309254" } },
        };
        var result = Ranker.Reclassify(MediaKind.Series, q, stored, false);
        Assert.Equal(FindingStatus.Proposed, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("tmdb:44006", result.RecommendedCandidateId);
        Assert.Equal(2, result.Candidates[0].ProviderIds.Count);
        Assert.Equal(2, result.Candidates[1].Providers.Count);
        Assert.All(result.Candidates, c => Assert.DoesNotContain(c.ProviderIds.Keys, k => k.StartsWith("SmartMatch", StringComparison.Ordinal)));
    }

    [Fact]
    public void Ambiguous_the_office_without_year_needs_review_and_is_never_automatic()
    {
        var q = Query("The.Office.S03E05.720p.WEB-DL.mkv", MediaKind.Series, NameSourceKind.EpisodeFile);
        var result = Ranker.Classify(MediaKind.Series, q, new[] { C("The Office", 2005, "2316"), C("The Office", 2001, "2996") }, false);
        Assert.Equal(FindingStatus.NeedsReview, result.Status);
        Assert.All(result.Candidates, c => Assert.False(c.AutoEligible));
    }

    [Fact]
    public void Catalogue_title_with_single_result_and_no_year_still_needs_review()
    {
        var result = Ranker.Classify(MediaKind.Series, Query("The Office", MediaKind.Series, NameSourceKind.SeriesFolder), new[] { C("The Office", 2005, "2316") }, false);
        Assert.Equal(FindingStatus.NeedsReview, result.Status);
        Assert.False(result.Candidates[0].AutoEligible);
    }

    [Fact]
    public void A_perfect_score_without_year_is_not_enough_for_automation()
    {
        var result = Ranker.Classify(MediaKind.Series, Query("The Fall and Rise of Reggie Dinkins", MediaKind.Series, NameSourceKind.SeriesFolder), new[] { C("The Fall and Rise of Reggie Dinkins", 2026, "300001") }, false);
        Assert.Equal(FindingStatus.Proposed, result.Status);
        Assert.False(result.Candidates[0].AutoEligible);
        Assert.Contains(result.AutoBlockers, b => b.Contains("no trusted year", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_year_prevents_cross_identification_of_remakes()
    {
        var results = new[] { C("Dune", 1984, "841"), C("Dune", 2021, "438631") };
        var r84 = Ranker.Classify(MediaKind.Movie, Query("Dune.1984.1080p.mkv", source: NameSourceKind.MovieFile), results, false);
        var r21 = Ranker.Classify(MediaKind.Movie, Query("Dune.2021.2160p.HEVC.mkv", source: NameSourceKind.MovieFile), results, false);
        Assert.Equal("tmdb:841", r84.RecommendedCandidateId);
        Assert.Equal("tmdb:438631", r21.RecommendedCandidateId);
        Assert.Contains(r84.Candidates, c => c.CandidateId == "tmdb:438631" && c.YearConflict);
    }

    [Fact]
    public void Doctor_who_with_year_resolves_and_without_year_does_not()
    {
        var results = new[] { C("Doctor Who", 1963, "121"), C("Doctor Who", 2005, "57243") };
        var withYear = Ranker.Classify(MediaKind.Series, Query("Doctor Who (2005)", MediaKind.Series, NameSourceKind.SeriesFolder), results, false);
        var noYear = Ranker.Classify(MediaKind.Series, Query("Doctor Who", MediaKind.Series, NameSourceKind.SeriesFolder), results, false);
        Assert.Equal("tmdb:57243", withYear.RecommendedCandidateId);
        Assert.Equal(FindingStatus.NeedsReview, noYear.Status);
        Assert.False(noYear.Candidates.Any(c => c.AutoEligible));
    }

    [Fact]
    public void Candidate_without_provider_id_is_review_only()
    {
        var result = Ranker.Classify(MediaKind.Movie, Query("Obscure Film (1999)"), new[] { C("Obscure Film", 1999, null) }, false);
        Assert.Equal(FindingStatus.NeedsReview, result.Status);
        Assert.False(result.Candidates[0].Applicable);
        Assert.Null(result.RecommendedCandidateId);
    }

    [Fact]
    public void Year_conflict_beyond_tolerance_is_rejected()
    {
        var result = Ranker.Classify(MediaKind.Movie, Query("Some Film (1990)"), new[] { C("Some Film", 2015, "1") }, false);
        Assert.Equal(FindingStatus.NeedsReview, result.Status);
        Assert.True(result.Candidates[0].YearConflict);
    }

    [Fact]
    public void Year_within_one_is_partial_credit_and_not_automatic()
    {
        var result = Ranker.Classify(MediaKind.Movie, Query("Some Film (2019)"), new[] { C("Some Film", 2020, "1") }, false);
        Assert.Equal(FindingStatus.Proposed, result.Status);
        Assert.Equal(85, result.Candidates[0].RankScore);
        Assert.False(result.Candidates[0].AutoEligible);
    }

    [Fact]
    public void Near_tie_needs_review()
    {
        var result = Ranker.Classify(MediaKind.Movie, Query("Shadow (2018)"), new[] { C("Shadow", 2018, "1"), C("Shadow", 2018, "2") }, false);
        Assert.Equal(FindingStatus.NeedsReview, result.Status);
        Assert.Contains("near-equivalent rival", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_id_from_two_providers_is_deduplicated_and_corroborated()
    {
        var result = Ranker.Classify(MediaKind.Movie, Query("Arrival (2016)"), new[]
        {
            C("Arrival", 2016, "329865", "TheMovieDb", "tt2543164"),
            C("Arrival", 2016, null, "OMDb", "tt2543164"),
        }, false);
        Assert.Single(result.Candidates);
        var top = result.Candidates[0];
        Assert.Equal(100, top.RankScore);
        Assert.Equal(2, top.Providers.Count);
        Assert.Equal("tt2543164", top.ProviderIds["Imdb"]);
    }

    [Fact]
    public void Partial_provider_failure_blocks_automation()
    {
        var result = Ranker.Classify(MediaKind.Movie, Query("Arrival (2016)"), new[] { C("Arrival", 2016, "329865") }, anyProviderFailed: true);
        Assert.Equal(FindingStatus.Proposed, result.Status);
        Assert.False(result.Candidates[0].AutoEligible);
    }

    [Fact]
    public void Weak_similarity_is_not_proposed()
    {
        var result = Ranker.Classify(MediaKind.Movie, Query("Arrival (2016)"), new[] { C("Arrivederci Amore, Ciao", 2006, "1") }, false);
        Assert.NotEqual(FindingStatus.Proposed, result.Status);
    }

    [Fact]
    public void No_results_is_no_match()
    {
        var result = Ranker.Classify(MediaKind.Movie, Query("Nothing (2016)"), Array.Empty<ProviderCandidate>(), false);
        Assert.Equal(FindingStatus.NoMatch, result.Status);
    }

    [Fact]
    public void Candidates_are_bounded_to_ten()
    {
        var many = Enumerable.Range(1, 30).Select(i => C("Show " + i, 2000, i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var result = Ranker.Classify(MediaKind.Movie, Query("Show (2000)"), many, false);
        Assert.Equal(10, result.Candidates.Count);
    }

    [Fact]
    public void Validator_drops_unknown_namespaces_and_bad_values()
    {
        var v = new ProviderIdValidator();
        var ids = v.FilterWritable(MediaKind.Movie, new Dictionary<string, string> { ["tmdb"] = "123", ["Imdb"] = "nope", ["Evil"] = "x", ["Tvdb"] = " 77 " });
        Assert.Equal("123", ids["Tmdb"]);
        Assert.Equal("77", ids["Tvdb"]);
        Assert.False(ids.ContainsKey("Imdb"));
        Assert.False(ids.ContainsKey("Evil"));
    }

    [Fact]
    public void Validator_extra_keys_are_opt_in()
    {
        var v = new ProviderIdValidator(new[] { "AniDB" });
        Assert.Equal("42", v.FilterWritable(MediaKind.Series, new Dictionary<string, string> { ["AniDB"] = "42" })["AniDB"]);
        Assert.Empty(new ProviderIdValidator().FilterWritable(MediaKind.Series, new Dictionary<string, string> { ["AniDB"] = "42" }));
    }

    [Theory]
    [InlineData("Tmdb", "603", true)]
    [InlineData("Imdb", "tt0133093", true)]
    [InlineData("Imdb", "garbage", false)]
    [InlineData("TmdbCollection", "1", false)]
    [InlineData("AniDB", "1", true)]
    [InlineData("Tmdb", "", false)]
    public void Reliable_identity_detection(string key, string value, bool expected)
    {
        Assert.Equal(expected, new ProviderIdValidator().HasReliableIdentity(new Dictionary<string, string> { [key] = value }));
    }

    [Fact]
    public async Task Executor_retries_transient_failures_then_reports_them()
    {
        var time = new FakeTimeProvider();
        using var executor = new ProviderSearchExecutor(NullLogger<ProviderSearchExecutor>.Instance, time, (d, ct) => Task.CompletedTask);
        var calls = 0;
        var context = new ProviderSearchContext(new[]
        {
            new SearchProviderHandle("Flaky", (t, y, ct) =>
            {
                calls++;
                throw Harness.Http(HttpStatusCode.TooManyRequests);
            }),
        }, "fp");
        var outcome = await executor.SearchAsync(context, "Title", null, new SearchBudget(TimeSpan.Zero, TimeSpan.FromSeconds(5), 3), CancellationToken.None);
        Assert.Equal(3, calls);
        Assert.True(outcome.AllFailed);
        Assert.Equal("RATE_LIMITED", outcome.Failures[0].Code);
    }

    [Fact]
    public async Task Executor_does_not_retry_client_errors()
    {
        using var executor = new ProviderSearchExecutor(NullLogger<ProviderSearchExecutor>.Instance, new FakeTimeProvider(), (d, ct) => Task.CompletedTask);
        var calls = 0;
        var context = new ProviderSearchContext(new[] { new SearchProviderHandle("P", (t, y, ct) => { calls++; throw Harness.Http(HttpStatusCode.NotFound); }) }, "fp");
        var outcome = await executor.SearchAsync(context, "Title", null, new SearchBudget(TimeSpan.Zero, TimeSpan.FromSeconds(5), 3), CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.False(outcome.Failures[0].Transient);
    }

    [Fact]
    public async Task Executor_enforces_its_own_timeout_even_if_provider_ignores_cancellation()
    {
        using var executor = new ProviderSearchExecutor(NullLogger<ProviderSearchExecutor>.Instance, TimeProvider.System);
        var hang = new TaskCompletionSource<IReadOnlyList<ProviderCandidate>>();
        var context = new ProviderSearchContext(new[] { new SearchProviderHandle("Slow", (t, y, ct) => hang.Task) }, "fp");
        var started = DateTime.UtcNow;
        var outcome = await executor.SearchAsync(context, "Title", null, new SearchBudget(TimeSpan.Zero, TimeSpan.FromMilliseconds(200), 1), CancellationToken.None);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal("TIMEOUT", outcome.Failures[0].Code);
    }

    [Fact]
    public async Task Executor_serialises_requests_with_minimum_interval()
    {
        var delays = new List<TimeSpan>();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var executor = new ProviderSearchExecutor(NullLogger<ProviderSearchExecutor>.Instance, time, (d, ct) => { delays.Add(d); return Task.CompletedTask; });
        var context = new ProviderSearchContext(new[] { new SearchProviderHandle("P", (t, y, ct) => Task.FromResult<IReadOnlyList<ProviderCandidate>>(Array.Empty<ProviderCandidate>())) }, "fp");
        var budget = new SearchBudget(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), 1);
        await executor.SearchAsync(context, "A", null, budget, CancellationToken.None);
        await executor.SearchAsync(context, "B", null, budget, CancellationToken.None);
        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromMilliseconds(500), delays[0]);
    }
}
