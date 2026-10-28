using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Configuration;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Normalization;
using Jellyfin.Plugin.SmartMatch.Services;
using Xunit;

namespace SmartMatch.UnitTests;

/// <summary>
/// End-to-end behaviour of the shared pipeline against fake Jellyfin boundaries.
/// Test names reference the PRD acceptance tests they cover.
/// </summary>
public class PipelineTests
{
    private const string Reggie = "/media/tv/www.UIndex.org - The Fall and Rise of Reggie Dinkins";

    private static (Harness H, Guid Tv, Guid Movies) Setup()
    {
        var h = new Harness();
        var tv = h.Library.AddLibrary("TV");
        var movies = h.Library.AddLibrary("Movies", "movies");
        h.Providers.Add(MediaKind.Series, "The Fall and Rise of Reggie Dinkins", 2026, "300001");
        h.Providers.Add(MediaKind.Series, "The Office", 2005, "2316");
        h.Providers.Add(MediaKind.Series, "The Office", 2001, "2996");
        h.Providers.Add(MediaKind.Movie, "The Dark Knight", 2008, "155");
        h.Providers.Add(MediaKind.Movie, "Dune", 1984, "841");
        h.Providers.Add(MediaKind.Movie, "Dune", 2021, "438631");
        return (h, tv, movies);
    }

    [Fact]
    public async Task AT02_prefixed_series_cleans_and_finds_candidate()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var outcome = await h.Analyze(item.Id);
        var f = outcome.Finding!;
        Assert.Equal("The Fall and Rise of Reggie Dinkins", f.SearchTitle);
        Assert.Equal(FindingStatus.Proposed, f.Status);
        Assert.Equal("tmdb:300001", f.RecommendedCandidateId);
        Assert.Equal(("The Fall and Rise of Reggie Dinkins", (int?)null), h.Providers.Queries.Single());
        Assert.DoesNotContain(h.Providers.Queries, q => q.Title.Contains('/', StringComparison.Ordinal));
    }

    [Fact]
    public async Task AT03_AT21_audit_and_scan_in_report_only_mode_never_mutate_metadata()
    {
        var (h, tv, movies) = Setup();
        using var _h = h;
        var a = h.Library.Add(tv, MediaKind.Series, Reggie);
        var b = h.Library.Add(movies, MediaKind.Movie, "/media/movies/The.Dark.Knight.2008.1080p.BluRay.x264/The.Dark.Knight.2008.1080p.BluRay.x264.mkv");
        h.Audits.StartAudit(RunKinds.InitialAudit, "system", false);
        h.Events.Subscribe();
        h.Observer.Start();
        h.Events.Raise(a.Id);
        await h.Drain();
        Assert.Equal(0, h.Library.Writes);
        Assert.Equal(0, h.Library.Refreshes);
        Assert.Empty(a.ProviderIds);
        Assert.Empty(b.ProviderIds);
        Assert.Equal(FindingStatus.Proposed, h.FindingFor(b.Id).Status);
        Assert.True(h.FindingFor(b.Id).Recommended()!.AutoEligible);
        Assert.True(h.Store.GetState().InitialAuditCompleted);
    }

    [Fact]
    public async Task AT04_approve_candidate_persists_id_refreshes_and_keeps_path()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        var result = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Equal("300001", item.ProviderIds["Tmdb"]);
        Assert.Equal("The Fall and Rise of Reggie Dinkins", item.Name);
        Assert.Equal(Reggie, item.Path);
        Assert.Equal(1, h.Library.Refreshes);
        var decision = h.Store.GetDecision(result.DecisionId!.Value)!;
        Assert.Equal(ApplyOrigin.Manual, decision.Origin);
        Assert.Equal("www.UIndex.org - The Fall and Rise of Reggie Dinkins", decision.BeforeTitle);
        Assert.Equal("300001", decision.WrittenProviderIds["Tmdb"]);
    }

    [Fact]
    public async Task AT05_identified_item_is_skipped_and_unchanged()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, "/media/tv/www.UIndex.org - Something", ids: new Dictionary<string, string> { ["Tvdb"] = "123" });
        var outcome = await h.Analyze(item.Id);
        Assert.Equal("Skipped:ALREADY_IDENTIFIED", outcome.Code);
        Assert.Empty(h.Providers.Queries);
        Assert.Null(h.Store.GetFindingByItem(item.Id));
    }

    [Fact]
    public async Task AT06_the_office_requires_review_and_auto_never_applies_it()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        h.EnableAuto(tv);
        var item = h.Library.Add(tv, MediaKind.Series, "/media/tv/The Office");
        h.Store.Enqueue(item.Id, FindingOrigin.NewScan, null, h.Time.GetUtcNow().UtcDateTime, false);
        await h.Drain();
        Assert.Equal(FindingStatus.NeedsReview, h.FindingFor(item.Id).Status);
        Assert.Equal(0, h.Library.Writes);
    }

    [Fact]
    public async Task AT07_conflicting_id_appearing_before_approval_aborts_safely()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        item.ProviderIds["Tvdb"] = "999";
        var result = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Conflict, result.Outcome);
        Assert.Equal("CONFLICTING_ID", result.Code);
        Assert.Equal(0, h.Library.Writes);
        Assert.Equal(FindingStatus.Conflict, h.FindingFor(item.Id).Status);
    }

    [Fact]
    public async Task Stale_ui_version_and_renamed_items_are_conflicts()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        var stale = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version - 1 + 5, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal("STALE_VERSION", stale.Code);

        item.Path = "/media/tv/renamed";
        var moved = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal("PATH_CHANGED", moved.Code);

        h.Library.Items.TryRemove(item.Id, out _);
        var f2 = h.FindingFor(item.Id);
        var gone = await h.Apply.ApplyAsync(new ApplyRequest(f2.ReviewId, null, f2.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Conflict, gone.Outcome);
        Assert.Equal(0, h.Library.Writes);
    }

    [Fact]
    public async Task Locked_items_are_never_processed_or_written()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        item.IsLocked = true;
        Assert.Equal("Skipped:LOCKED", (await h.Analyze(item.Id)).Code);
        item.IsLocked = false;
        var f = (await h.Analyze(item.Id)).Finding!;
        item.IsLocked = true;
        var result = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal("LOCKED", result.Code);
        Assert.Equal(0, h.Library.Writes);
    }

    [Fact]
    public async Task Locked_name_keeps_the_title_but_still_identifies()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        item.NameLocked = true;
        var f = (await h.Analyze(item.Id)).Finding!;
        var result = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Equal("www.UIndex.org - The Fall and Rise of Reggie Dinkins", item.Name);
    }

    [Fact]
    public async Task AT08_AT25_provider_failure_is_retriable_and_never_modifies_item()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        h.Providers.Fail = () => Harness.Http(HttpStatusCode.ServiceUnavailable);
        var outcome = await h.Analyze(item.Id);
        var f = outcome.Finding!;
        Assert.Equal(FindingStatus.ProviderError, f.Status);
        Assert.Equal("PROVIDER_5XX", f.ReasonCode);
        Assert.NotNull(outcome.DeferUntilUtc);
        Assert.Equal(0, h.Library.Writes);

        // Back-off: no new search before the retry time, then success.
        var again = await h.Analyze(item.Id);
        Assert.Equal("Deferred:PROVIDER_BACKOFF", again.Code);
        h.Providers.Fail = null;
        h.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(FindingStatus.Proposed, (await h.Analyze(item.Id)).Finding!.Status);
    }

    [Fact]
    public async Task Provider_backoff_does_not_keep_a_run_open_and_retries_later()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        h.Providers.Fail = () => Harness.Http(HttpStatusCode.ServiceUnavailable);
        var run = h.Audits.StartAudit(RunKinds.Audit, "admin", false);
        await h.Drain();
        Assert.Equal(RunStatus.Completed, h.Store.GetAuditRun(run.RunId)!.Status);
        Assert.Equal(1, h.Store.QueueCount());
        Assert.Equal(FindingStatus.ProviderError, h.FindingFor(item.Id).Status);

        // A second audit during the back-off window also completes instead of waiting for the retry.
        var second = h.Audits.StartAudit(RunKinds.Audit, "admin", false);
        await h.Worker.DrainAsync(CancellationToken.None);
        Assert.Equal(RunStatus.Completed, h.Store.GetAuditRun(second.RunId)!.Status);

        h.Providers.Fail = null;
        h.Time.Advance(TimeSpan.FromMinutes(20));
        await h.Drain();
        Assert.Equal(FindingStatus.Proposed, h.FindingFor(item.Id).Status);
        Assert.Equal(0, h.Store.QueueCount());
    }

    [Fact]
    public async Task No_enabled_providers_is_reported_distinctly()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        h.Providers.ProviderNames.Clear();
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        Assert.Equal("NO_PROVIDERS", f.ReasonCode);
    }

    [Fact]
    public async Task AT10_restart_after_write_keeps_outcome_and_does_not_reapply()
    {
        var (h, tv, _) = Setup();
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        await h.Store.FlushAsync(CancellationToken.None);

        using var restarted = new Harness(h.Directory);
        foreach (var (k, v) in h.Library.Items)
        {
            restarted.Library.Items[k] = v;
        }

        restarted.Library.Libraries.AddRange(h.Library.Libraries);
        restarted.Providers.Catalogue.AddRange(h.Providers.Catalogue);
        Assert.Equal(FindingStatus.Applied, restarted.FindingFor(item.Id).Status);
        Assert.NotEmpty(restarted.Store.GetDecisions(null, 0, 10, out _));
        var outcome = await restarted.Analyze(item.Id);
        Assert.Equal("Skipped:ALREADY_IDENTIFIED", outcome.Code);
        Assert.Equal(0, restarted.Library.Writes);
        h.Dispose();
    }

    private const string ChicagoFire = "/media/tv/www.UIndex.org - Chicago.Fire.S14E08.1080p.WEB.h264-ETHEL";

    private static (Harness H, Guid Tv) SetupTwoProviders()
    {
        var (h, tv, _) = Setup();
        h.Providers.ProviderNames.Add("The Open Movie Database");
        h.Providers.Add(MediaKind.Series, "Chicago Fire", 2012, "44006");
        h.Providers.Catalogue.Add(("Chicago Fire", 2012, MediaKind.Series, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt2261391" }, "The Open Movie Database"));
        return (h, tv);
    }

    /// <summary>
    /// Rewrites the Chicago Fire finding with the two candidates an older ranker produced, then
    /// restarts on the same journal.
    /// </summary>
    private static async Task<Harness> RestartWithOldChicagoFire(Harness h, Guid itemId, bool reportedIds)
    {
        var f = h.FindingFor(itemId);
        h.Store.TryUpdateFinding(f.ReviewId, null, x =>
        {
            x.RankerRevision = reportedIds ? 1 : 0;
            x.Status = FindingStatus.NeedsReview;
            x.Candidates = new List<CandidateRecord>
            {
                new() { CandidateId = "tmdb:44006", Name = "Chicago Fire", Year = 2012, ProviderName = "TheMovieDb", Providers = new() { "TheMovieDb" }, ProviderIds = new() { ["Tmdb"] = "44006" }, ReportedProviderIds = reportedIds ? new() { ["Tmdb"] = "44006" } : null },
                new() { CandidateId = "imdb:tt2261391", Name = "Chicago Fire", Year = 2012, ProviderName = "The Open Movie Database", Providers = new() { "The Open Movie Database" }, ProviderIds = new() { ["Imdb"] = "tt2261391" }, ReportedProviderIds = reportedIds ? new() { ["Imdb"] = "tt2261391" } : null },
            };
            return true;
        }, out _);
        await h.Store.FlushAsync(CancellationToken.None);

        var restarted = new Harness(h.Directory);
        foreach (var (k, v) in h.Library.Items)
        {
            restarted.Library.Items[k] = v;
        }

        restarted.Library.Libraries.AddRange(h.Library.Libraries);
        restarted.Providers.ProviderNames.Add("The Open Movie Database");
        restarted.Providers.Catalogue.AddRange(h.Providers.Catalogue);
        restarted.Worker.RecoverForTests();
        return restarted;
    }

    [Fact]
    public async Task Ranking_update_reranks_open_findings_at_startup_without_searching()
    {
        var (h, tv) = SetupTwoProviders();
        var fire = h.Library.Add(tv, MediaKind.Series, ChicagoFire);
        var office = h.Library.Add(tv, MediaKind.Series, "/media/tv/The Office");
        Assert.Equal(FindingStatus.Proposed, (await h.Analyze(fire.Id)).Finding!.Status);
        var o = (await h.Analyze(office.Id)).Finding!;
        h.Store.TryUpdateFinding(o.ReviewId, null, x =>
        {
            x.RankerRevision = 1;
            x.Status = FindingStatus.Ignored;
            return true;
        }, out _);

        using var restarted = await RestartWithOldChicagoFire(h, fire.Id, reportedIds: true);
        var reranked = restarted.FindingFor(fire.Id);
        Assert.Equal(FindingStatus.Proposed, reranked.Status);
        Assert.Equal(MatchCandidateRanker.Revision, reranked.RankerRevision);
        Assert.Equal("tmdb:44006", reranked.RecommendedCandidateId);
        Assert.Single(reranked.Candidates);
        Assert.Equal(FindingStatus.Ignored, restarted.FindingFor(office.Id).Status);
        Assert.Empty(restarted.Providers.Queries);
        Assert.Equal(0, restarted.Library.Writes);

        // Nothing was queued, and the next scan of the unchanged item does not search either.
        await restarted.Drain();
        Assert.Equal("Unchanged", (await restarted.Analyze(fire.Id)).Code);
        Assert.Empty(restarted.Providers.Queries);
        h.Dispose();
    }

    [Fact]
    public async Task Findings_saved_without_reported_ids_are_not_promoted_but_searched_once_more()
    {
        var (h, tv) = SetupTwoProviders();
        var fire = h.Library.Add(tv, MediaKind.Series, ChicagoFire);
        await h.Analyze(fire.Id);

        using var restarted = await RestartWithOldChicagoFire(h, fire.Id, reportedIds: false);
        var reranked = restarted.FindingFor(fire.Id);
        Assert.Equal(FindingStatus.NeedsReview, reranked.Status);
        Assert.Equal(2, reranked.Candidates.Count);
        Assert.Empty(restarted.Providers.Queries);

        // The fresh analysis sees every reported id and makes one search per provider.
        await restarted.Drain();
        var fresh = restarted.FindingFor(fire.Id);
        Assert.Equal(FindingStatus.Proposed, fresh.Status);
        Assert.Single(fresh.Candidates);
        Assert.NotNull(fresh.Candidates[0].ReportedProviderIds);
        Assert.Equal(2, restarted.Providers.Queries.Count);
        Assert.Equal(0, restarted.Library.Writes);
        h.Dispose();
    }

    [Fact]
    public async Task Items_with_the_same_query_share_one_search_for_a_day()
    {
        var (h, tv) = SetupTwoProviders();
        using var _h = h;
        var e8 = h.Library.Add(tv, MediaKind.Series, ChicagoFire);
        var e9 = h.Library.Add(tv, MediaKind.Series, "/media/tv/www.UIndex.org - Chicago.Fire.S14E09.1080p.WEB.h264-ETHEL");
        var e10 = h.Library.Add(tv, MediaKind.Series, "/media/tv/www.UIndex.org - Chicago.Fire.S14E10.1080p.WEB.h264-ETHEL");

        Assert.True((await h.Analyze(e8.Id)).Searched);
        var second = await h.Analyze(e9.Id);
        Assert.False(second.Searched);
        Assert.Equal(FindingStatus.Proposed, second.Finding!.Status);
        Assert.Equal(2, h.Providers.Queries.Count);

        // A manual Retry analysis always asks the providers again.
        Assert.True((await h.Analyze(e9.Id, FindingOrigin.Manual, force: true)).Searched);
        Assert.Equal(4, h.Providers.Queries.Count);

        // After a day the cached search has expired.
        h.Time.Advance(ProviderResultCache.Lifetime);
        Assert.True((await h.Analyze(e10.Id)).Searched);
        Assert.Equal(6, h.Providers.Queries.Count);
    }

    [Fact]
    public async Task Searches_with_a_provider_failure_are_not_cached()
    {
        var (h, tv) = SetupTwoProviders();
        using var _h = h;
        var e8 = h.Library.Add(tv, MediaKind.Series, ChicagoFire);
        var e9 = h.Library.Add(tv, MediaKind.Series, "/media/tv/www.UIndex.org - Chicago.Fire.S14E09.1080p.WEB.h264-ETHEL");
        h.Providers.FailProvider["The Open Movie Database"] = () => Harness.Http(HttpStatusCode.ServiceUnavailable);

        await h.Analyze(e8.Id);
        h.Providers.FailProvider.Clear();
        Assert.True((await h.Analyze(e9.Id)).Searched);
    }

    [Fact]
    public async Task AT12_sidecar_library_warns_and_blocks_unattended_automation()
    {
        var h = new Harness();
        using var _h = h;
        var tv = h.Library.AddLibrary("TV", sidecar: true);
        h.Providers.Add(MediaKind.Series, "Severance", 2022, "95396");
        h.EnableAuto(tv);
        var item = h.Library.Add(tv, MediaKind.Series, "/tv/www.UIndex.org - Severance (2022)");
        h.Store.Enqueue(item.Id, FindingOrigin.NewScan, null, h.Time.GetUtcNow().UtcDateTime, false);
        await h.Drain();
        var f = h.FindingFor(item.Id);
        Assert.True(f.SidecarRisk);
        Assert.True(f.Recommended()!.AutoEligible);
        Assert.Equal(0, h.Library.Writes);

        // Manual preview shows the warning; acknowledging the library re-enables automation.
        var plan = h.Bulk.Preview(new BatchPreviewRequest { Scope = "Selected", ReviewIds = { f.ReviewId } }, "admin");
        Assert.Contains(plan.Items.Single().Warnings, w => w.Contains("NFO", StringComparison.Ordinal));
        h.Config.Current.SidecarAcknowledgedLibraryIds = new[] { tv.ToString() };
        var auto = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Automatic, "automatic"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Applied, auto.Outcome);
    }

    [Fact]
    public async Task Local_nfo_blocks_automation_but_not_manual_apply()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        h.Providers.Add(MediaKind.Series, "Severance", 2022, "95396");
        h.EnableAuto(tv);
        var item = h.Library.Add(tv, MediaKind.Series, "/tv/Severance (2022)");
        item.HasLocalNfo = true;
        var f = (await h.Analyze(item.Id)).Finding!;
        var auto = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Automatic, "automatic"), CancellationToken.None);
        Assert.Equal("LOCAL_NFO", auto.Code);
        var manual = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Applied, manual.Outcome);
    }

    [Fact]
    public async Task AT13_movie_release_filename_keeps_title_and_year()
    {
        var (h, _, movies) = Setup();
        using var _h = h;
        var item = h.Library.Add(movies, MediaKind.Movie, "/movies/The.Dark.Knight.2008.1080p.BluRay.x264/The.Dark.Knight.2008.1080p.BluRay.x264.mkv");
        var f = (await h.Analyze(item.Id)).Finding!;
        Assert.Equal("The Dark Knight", f.SearchTitle);
        Assert.Equal(2008, f.TrustedYear);
        Assert.Equal("tmdb:155", f.RecommendedCandidateId);
    }

    [Fact]
    public async Task Mixed_folder_movie_uses_filename_and_folder_year_corroborates()
    {
        var (h, _, movies) = Setup();
        using var _h = h;
        var mixed = h.Library.Add(movies, MediaKind.Movie, "/movies/All Films/Dune.1984.1080p.mkv");
        mixed.IsInMixedFolder = true;
        Assert.Equal("tmdb:841", (await h.Analyze(mixed.Id)).Finding!.RecommendedCandidateId);

        var folder = h.Library.Add(movies, MediaKind.Movie, "/movies/Dune (2021)/dune-uhd.mkv");
        Assert.Equal("tmdb:438631", (await h.Analyze(folder.Id)).Finding!.RecommendedCandidateId);
    }

    [Fact]
    public async Task AT14_no_media_path_changes_through_any_path()
    {
        var (h, tv, movies) = Setup();
        using var _h = h;
        var items = new[]
        {
            h.Library.Add(tv, MediaKind.Series, Reggie),
            h.Library.Add(movies, MediaKind.Movie, "/movies/The.Dark.Knight.2008.1080p.BluRay.x264/The.Dark.Knight.2008.1080p.BluRay.x264.mkv"),
        };
        var paths = items.Select(i => i.Path).ToList();
        h.EnableAuto(movies);
        h.Audits.StartAudit(RunKinds.Audit, "admin", false);
        await h.Drain();
        var plan = h.Bulk.Preview(new BatchPreviewRequest { Scope = "AllEligible" }, "admin");
        if (plan.Items.Count > 0)
        {
            var run = h.Bulk.Commit(plan.PlanId, plan.PlanHash, Guid.NewGuid().ToString(), "admin");
            await h.Bulk.WaitAsync(run.RunId);
        }

        Assert.Equal(paths, items.Select(i => i.Path).ToList());
        Assert.All(h.Library.Log, l => Assert.DoesNotContain("rename", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AT17_scan_event_enqueues_and_report_appears_without_manual_audit()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        h.Observer.Start();
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var before = DateTime.UtcNow;
        h.Events.Raise(item.Id, "ItemAdded");
        Assert.True(DateTime.UtcNow - before < TimeSpan.FromMilliseconds(200), "event handler must return immediately");
        Assert.Empty(h.Providers.Queries);
        Assert.Equal(1, h.Store.QueueCount());
        await h.Drain();
        var f = h.FindingFor(item.Id);
        Assert.Equal(FindingOrigin.NewScan, f.Origin);
        Assert.Equal(FindingStatus.Proposed, f.Status);
    }

    [Fact]
    public async Task Busy_items_and_running_scans_are_deferred()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        h.Library.Busy.Add(item.Id);
        var outcome = await h.Analyze(item.Id);
        Assert.StartsWith("Deferred:BUSY", outcome.Code, StringComparison.Ordinal);
        Assert.Empty(h.Providers.Queries);
    }

    [Fact]
    public async Task AT18_preexisting_items_are_discovered_by_initial_audit_and_persist()
    {
        var (h, tv, movies) = Setup();
        var a = h.Library.Add(tv, MediaKind.Series, Reggie);
        var b = h.Library.Add(movies, MediaKind.Movie, "/movies/Dune.2021.2160p.HEVC/Dune.2021.2160p.HEVC.mkv");
        var run = h.Audits.StartAudit(RunKinds.InitialAudit, "system", false);
        Assert.Equal(2, run.Enqueued);
        await h.Drain();
        await h.Store.FlushAsync(CancellationToken.None);
        Assert.Equal(RunStatus.Completed, h.Store.GetAuditRun(run.RunId)!.Status);
        using var restarted = new Harness(h.Directory);
        Assert.NotNull(restarted.Store.GetFindingByItem(a.Id));
        Assert.NotNull(restarted.Store.GetFindingByItem(b.Id));
        Assert.True(restarted.Store.GetState().InitialAuditCompleted);
        h.Dispose();
    }

    [Fact]
    public async Task Initial_audit_resumes_after_restart()
    {
        var (h, tv, _) = Setup();
        for (var i = 0; i < 5; i++)
        {
            h.Library.Add(tv, MediaKind.Series, "/tv/Show Number " + i);
        }

        var run = h.Audits.StartAudit(RunKinds.InitialAudit, "system", false);
        await h.Store.FlushAsync(CancellationToken.None);

        using var restarted = new Harness(h.Directory);
        foreach (var (k, v) in h.Library.Items)
        {
            restarted.Library.Items[k] = v;
        }

        restarted.Library.Libraries.AddRange(h.Library.Libraries);
        Assert.Equal(5, restarted.Store.QueueCountForRun(run.RunId));
        await restarted.Drain();
        Assert.Equal(RunStatus.Completed, restarted.Store.GetAuditRun(run.RunId)!.Status);
        Assert.True(restarted.Store.GetState().InitialAuditCompleted);
        h.Dispose();
    }

    [Fact]
    public async Task AT09_cancel_halfway_stops_safely_and_keeps_results()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        for (var i = 0; i < 10; i++)
        {
            h.Library.Add(tv, MediaKind.Series, "/tv/www.UIndex.org - Show " + i);
        }

        var run = h.Audits.StartAudit(RunKinds.Audit, "admin", false);
        var first = h.Store.PeekReady(h.Time.GetUtcNow().UtcDateTime, 3);
        foreach (var e in first)
        {
            await h.Analyzer.AnalyzeAsync(e, CancellationToken.None);
            h.Store.CompleteQueueEntry(e.ItemId, e.LastEventUtc);
        }

        var cancelled = h.Audits.Cancel(run.RunId)!;
        Assert.Equal(RunStatus.Cancelled, cancelled.Status);
        Assert.Equal(0, h.Store.QueueCount());
        Assert.Equal(3, h.Store.QueryFindings(_ => true).Count);
        Assert.Equal(0, await h.Drain());
    }

    [Fact]
    public async Task AT19_apply_selected_two_valid_one_ambiguous()
    {
        var (h, tv, movies) = Setup();
        using var _h = h;
        var a = h.Library.Add(tv, MediaKind.Series, Reggie);
        var b = h.Library.Add(movies, MediaKind.Movie, "/movies/The.Dark.Knight.2008.1080p.BluRay.x264/The.Dark.Knight.2008.1080p.BluRay.x264.mkv");
        var c = h.Library.Add(tv, MediaKind.Series, "/tv/The Office");
        foreach (var i in new[] { a, b, c })
        {
            await h.Analyze(i.Id);
        }

        var ids = new[] { a, b, c }.Select(i => h.FindingFor(i.Id).ReviewId).ToList();
        var plan = h.Bulk.Preview(new BatchPreviewRequest { Scope = "Selected", ReviewIds = ids }, "admin");
        Assert.Equal(2, plan.Items.Count);
        var exclusion = Assert.Single(plan.Exclusions);
        Assert.Contains("Ambiguous", exclusion.Reason, StringComparison.Ordinal);

        var run = h.Bulk.Commit(plan.PlanId, plan.PlanHash, "key-1", "admin");
        await h.Bulk.WaitAsync(run.RunId);
        var done = h.Store.GetBatchRun(run.RunId)!;
        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.All(done.Items, i => Assert.Equal(ApplyOutcome.Applied, i.Outcome));
        Assert.Empty(c.ProviderIds);
        Assert.Equal(FindingStatus.NeedsReview, h.FindingFor(c.Id).Status);
        Assert.All(h.Store.GetDecisions(null, 0, 10, out _).Where(d => d.Action == "Apply"), d => Assert.Equal(ApplyOrigin.Bulk, d.Origin));
    }

    [Fact]
    public async Task AT20_apply_all_eligible_in_filter_shows_scope_and_requires_confirmation()
    {
        var (h, tv, movies) = Setup();
        using var _h = h;
        var series = h.Library.Add(tv, MediaKind.Series, Reggie);
        var film = h.Library.Add(movies, MediaKind.Movie, "/movies/The.Dark.Knight.2008.1080p.BluRay.x264/The.Dark.Knight.2008.1080p.BluRay.x264.mkv");
        var office = h.Library.Add(tv, MediaKind.Series, "/tv/The Office");
        foreach (var i in new[] { series, film, office })
        {
            await h.Analyze(i.Id);
        }

        var filter = new FindingFilter { MediaType = MediaKind.Series };
        var plan = h.Bulk.Preview(new BatchPreviewRequest { Scope = "AllEligible", Filter = filter }, "admin");
        Assert.Contains("current filter", plan.ScopeDescription, StringComparison.Ordinal);
        Assert.Single(plan.Items);
        Assert.Equal(series.Id, h.Store.GetFinding(plan.Items[0].ReviewId)!.ItemId);
        Assert.Single(plan.Exclusions);

        // Preview alone changes nothing; confirmation with a wrong hash is refused.
        Assert.Equal(0, h.Library.Writes);
        Assert.Throws<BatchException>(() => h.Bulk.Commit(plan.PlanId, "wrong", "k", "admin"));
        var run = h.Bulk.Commit(plan.PlanId, plan.PlanHash, "k2", "admin");
        await h.Bulk.WaitAsync(run.RunId);
        Assert.Equal("300001", series.ProviderIds["Tmdb"]);
        Assert.Empty(film.ProviderIds);
        Assert.Empty(office.ProviderIds);
    }

    [Fact]
    public async Task Whole_report_scope_needs_explicit_scope_and_plans_expire_and_replay_is_idempotent()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var a = h.Library.Add(tv, MediaKind.Series, Reggie);
        await h.Analyze(a.Id);
        Assert.Throws<BatchException>(() => h.Bulk.Preview(new BatchPreviewRequest(), "admin"));
        var plan = h.Bulk.Preview(new BatchPreviewRequest { Scope = "AllEligible" }, "admin");
        Assert.Equal("all eligible findings in the entire report", plan.ScopeDescription);

        h.Time.Advance(TimeSpan.FromMinutes(16));
        var expired = Assert.Throws<BatchException>(() => h.Bulk.Commit(plan.PlanId, plan.PlanHash, "k", "admin"));
        Assert.Equal("PLAN_EXPIRED", expired.Code);

        var plan2 = h.Bulk.Preview(new BatchPreviewRequest { Scope = "AllEligible" }, "admin");
        var run = h.Bulk.Commit(plan2.PlanId, plan2.PlanHash, "same-key", "admin");
        var replay = h.Bulk.Commit(plan2.PlanId, plan2.PlanHash, "same-key", "admin");
        Assert.Equal(run.RunId, replay.RunId);
        await h.Bulk.WaitAsync(run.RunId);
        Assert.Equal(1, h.Library.Writes);
    }

    [Fact]
    public async Task Stale_plan_item_is_revalidated_and_reported_as_conflict()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var a = h.Library.Add(tv, MediaKind.Series, Reggie);
        await h.Analyze(a.Id);
        var plan = h.Bulk.Preview(new BatchPreviewRequest { Scope = "AllEligible" }, "admin");
        a.ProviderIds["Tvdb"] = "1";
        var run = h.Bulk.Commit(plan.PlanId, plan.PlanHash, "k", "admin");
        await h.Bulk.WaitAsync(run.RunId);
        Assert.Equal(ApplyOutcome.Conflict, h.Store.GetBatchRun(run.RunId)!.Items[0].Outcome);
        Assert.Equal(0, h.Library.Writes);
    }

    [Fact]
    public async Task AT26_bulk_interrupted_after_partial_completion()
    {
        var (h, tv, _) = Setup();
        var items = Enumerable.Range(0, 4).Select(i =>
        {
            h.Providers.Add(MediaKind.Series, "Show " + i + " Title", 2020, (5000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return h.Library.Add(tv, MediaKind.Series, "/tv/www.UIndex.org - Show " + i + " Title (2020)");
        }).ToList();
        foreach (var i in items)
        {
            await h.Analyze(i.Id);
        }

        var plan = h.Bulk.Preview(new BatchPreviewRequest { Scope = "AllEligible" }, "admin");
        Assert.Equal(4, plan.Items.Count);

        // Cancel after the second write.
        var writes = 0;
        h.Library.OnWrite = _ =>
        {
            if (++writes == 2)
            {
                h.Bulk.Cancel(h.Store.GetBatchRuns()[0].RunId);
            }
        };
        var run = h.Bulk.Commit(plan.PlanId, plan.PlanHash, "k", "admin");

        await h.Bulk.WaitAsync(run.RunId);
        var done = h.Store.GetBatchRun(run.RunId)!;
        Assert.Equal(RunStatus.Cancelled, done.Status);
        var applied = done.Items.Count(i => i.Outcome == ApplyOutcome.Applied);
        Assert.Equal(2, applied);
        Assert.All(done.Items.Where(i => i.Outcome != ApplyOutcome.Applied), i => Assert.Equal(ApplyOutcome.Cancelled, i.Outcome));

        // Pending items remain eligible in the report and a fresh preview retries exactly them.
        var retry = h.Bulk.Preview(new BatchPreviewRequest { Scope = "AllEligible" }, "admin");
        Assert.Equal(4 - applied, retry.Items.Count);
        h.Dispose();
    }

    [Fact]
    public async Task Interrupted_batch_and_apply_recover_after_restart()
    {
        var (h, tv, _) = Setup();
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        h.Store.SaveBatchRun(new BatchRun { RunId = Guid.NewGuid(), Status = RunStatus.Running, StartedAtUtc = DateTime.UtcNow, Items = { new BatchItem { ReviewId = f.ReviewId, CandidateId = "tmdb:300001" } } });
        var decision = new DecisionRecord { DecisionId = Guid.NewGuid(), ReviewId = f.ReviewId, TimeUtc = DateTime.UtcNow, WrittenProviderIds = { ["Tmdb"] = "300001" } };
        h.Store.AddDecision(decision, 30, 5000);
        h.Store.TryUpdateFinding(f.ReviewId, null, x => { x.Status = FindingStatus.Applying; x.LastDecisionId = decision.DecisionId; x.AppliedCandidateId = "tmdb:300001"; return true; }, out _);
        item.ProviderIds["Tmdb"] = "300001";
        await h.Store.FlushAsync(CancellationToken.None);

        using var restarted = new Harness(h.Directory);
        foreach (var (k, v) in h.Library.Items)
        {
            restarted.Library.Items[k] = v;
        }

        restarted.Library.Libraries.AddRange(h.Library.Libraries);
        restarted.Worker.RecoverForTests();
        Assert.Equal(RunStatus.Interrupted, restarted.Store.GetBatchRuns().Single().Status);
        var recovered = restarted.FindingFor(item.Id);
        Assert.Equal(FindingStatus.Partial, recovered.Status);

        // Verified retry repeats only the refresh.
        var retry = await restarted.Apply.ApplyAsync(new ApplyRequest(recovered.ReviewId, null, recovered.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Applied, retry.Outcome);
        Assert.Equal(0, restarted.Library.Writes);
        Assert.Equal(1, restarted.Library.Refreshes);
        h.Dispose();
    }

    [Fact]
    public async Task Refresh_failure_is_partial_and_retry_only_refreshes()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        h.Library.FailNextRefresh = new InvalidOperationException("provider down");
        var first = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Partial, first.Outcome);
        Assert.Equal("300001", item.ProviderIds["Tmdb"]);
        var partial = h.FindingFor(item.Id);
        var retry = await h.Apply.ApplyAsync(new ApplyRequest(partial.ReviewId, null, partial.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Applied, retry.Outcome);
        Assert.Equal(1, h.Library.Writes);
        Assert.Equal(2, h.Library.Refreshes);
    }

    [Fact]
    public async Task Write_failure_restores_status_and_writes_nothing()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        h.Library.FailWrites = new InvalidOperationException("db locked");
        var result = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Equal(FindingStatus.Proposed, h.FindingFor(item.Id).Status);
        Assert.Empty(item.ProviderIds);
    }

    [Fact]
    public async Task Concurrent_applications_of_the_same_item_write_once()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None))));
        Assert.Equal(1, results.Count(r => r.Outcome == ApplyOutcome.Applied));
        Assert.Equal(1, h.Library.Writes);
    }

    [Fact]
    public async Task Ambiguous_finding_can_be_applied_individually_with_an_explicit_candidate()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, "/tv/The Office");
        var f = (await h.Analyze(item.Id)).Finding!;
        var noChoice = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal("CANDIDATE_REQUIRED", noChoice.Code);
        var bulk = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, "tmdb:2996", f.Version, ApplyOrigin.Bulk, "admin"), CancellationToken.None);
        Assert.Equal("AMBIGUOUS", bulk.Code);
        var chosen = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, "tmdb:2996", f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Applied, chosen.Outcome);
        Assert.Equal("2996", item.ProviderIds["Tmdb"]);
    }

    [Fact]
    public async Task AT22_auto_apply_for_one_library_only_applies_high_confidence_there()
    {
        var (h, tv, movies) = Setup();
        using var _h = h;
        h.Providers.Add(MediaKind.Series, "Severance", 2022, "95396");
        h.EnableAuto(movies);
        var film = h.Library.Add(movies, MediaKind.Movie, "/movies/The.Dark.Knight.2008.1080p.BluRay.x264/The.Dark.Knight.2008.1080p.BluRay.x264.mkv");
        var otherLibrary = h.Library.Add(tv, MediaKind.Series, "/tv/www.UIndex.org - Severance (2022)");
        var ambiguous = h.Library.Add(movies, MediaKind.Movie, "/movies/Dune/Dune.mkv");
        h.Audits.StartAudit(RunKinds.Audit, "admin", false);
        await h.Drain();

        Assert.Equal("155", film.ProviderIds["Tmdb"]);
        Assert.Equal(FindingStatus.Applied, h.FindingFor(film.Id).Status);
        Assert.Equal(ApplyOrigin.Automatic, h.Store.GetDecisions(h.FindingFor(film.Id).ReviewId, 0, 5, out _).First(d => d.Action == "Apply").Origin);
        Assert.Empty(otherLibrary.ProviderIds);
        Assert.True(h.FindingFor(otherLibrary.Id).Recommended()!.AutoEligible);
        Assert.Empty(ambiguous.ProviderIds);
        Assert.Equal(FindingStatus.NeedsReview, h.FindingFor(ambiguous.Id).Status);
        Assert.Equal(1, h.Library.Refreshes);
    }

    [Fact]
    public async Task AT23_disabling_auto_apply_stops_queued_automatic_commits()
    {
        var (h, _, movies) = Setup();
        using var _h = h;
        h.EnableAuto(movies);
        var film = h.Library.Add(movies, MediaKind.Movie, "/movies/The.Dark.Knight.2008.1080p.BluRay.x264/The.Dark.Knight.2008.1080p.BluRay.x264.mkv");
        var f = (await h.Analyze(film.Id)).Finding!;
        Assert.True(f.Recommended()!.AutoEligible);

        // Admin disables auto-apply while an automatic commit is queued.
        h.Config.Current.Mode = ApplyMode.ReportOnly;
        var result = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Automatic, "automatic"), CancellationToken.None);
        Assert.Equal("AUTO_DISABLED", result.Code);
        Assert.Equal(0, h.Library.Writes);

        // Removing the library from the opt-in list has the same effect.
        h.Config.Current.Mode = ApplyMode.AutoApplyHighConfidence;
        h.Config.Current.AutoApplyLibraryIds = Array.Empty<string>();
        Assert.Equal("AUTO_DISABLED", (await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Automatic, "automatic"), CancellationToken.None)).Code);
    }

    [Fact]
    public async Task AT24_repeated_item_updated_and_refresh_events_cause_one_lookup()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        h.Observer.Start();
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        for (var i = 0; i < 20; i++)
        {
            h.Events.Raise(item.Id, i % 2 == 0 ? "ItemUpdated" : "RefreshCompleted");
        }

        await h.Drain();
        for (var i = 0; i < 20; i++)
        {
            h.Events.Raise(item.Id, "ItemUpdated");
        }

        await h.Drain();
        Assert.Single(h.Providers.Queries);
    }

    [Fact]
    public async Task Own_writes_do_not_trigger_reprocessing_loops()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        h.Observer.Start();
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        h.Library.OnWrite = i => h.Events.Raise(i.Id, "ItemUpdated");
        var f = (await h.Analyze(item.Id)).Finding!;
        await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        h.Events.Raise(item.Id, "RefreshCompleted", item.ProviderIds);
        Assert.Equal(0, h.Store.QueueCount());
        Assert.True(h.Status.EventsSuppressed >= 1);
    }

    [Fact]
    public async Task Rollback_restores_only_unchanged_plugin_owned_fields()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        var applied = h.FindingFor(item.Id);
        var rollback = await h.Apply.RollbackAsync(applied.ReviewId, applied.Version, "admin", CancellationToken.None);
        Assert.Equal(ApplyOutcome.RolledBack, rollback.Outcome);
        Assert.Empty(item.ProviderIds);
        Assert.Equal("www.UIndex.org - The Fall and Rise of Reggie Dinkins", item.Name);
        Assert.Equal(FindingStatus.RolledBack, h.FindingFor(item.Id).Status);

        // A rolled-back item is not re-proposed automatically.
        Assert.Equal("Skipped:PLUGIN_OWNED", (await h.Analyze(item.Id)).Code);
    }

    [Fact]
    public async Task Rollback_refuses_when_someone_else_changed_the_fields()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        item.Name = "Edited by a human";
        var applied = h.FindingFor(item.Id);
        var rollback = await h.Apply.RollbackAsync(applied.ReviewId, applied.Version, "admin", CancellationToken.None);
        Assert.Equal("CHANGED_SINCE_APPLY", rollback.Code);
        Assert.Equal("Edited by a human", item.Name);
        Assert.Equal("300001", item.ProviderIds["Tmdb"]);
    }

    [Fact]
    public async Task Item_identified_outside_the_plugin_resolves_its_finding()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        await h.Analyze(item.Id);
        item.ProviderIds["Tvdb"] = "42";
        await h.Analyze(item.Id);
        Assert.Equal(FindingStatus.Resolved, h.FindingFor(item.Id).Status);
    }

    [Fact]
    public async Task Ignored_items_are_not_reproposed_and_rule_changes_trigger_new_search()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, "/tv/mysite.example - Severance (2022)");
        h.Providers.Add(MediaKind.Series, "Severance", 2022, "95396");
        var first = (await h.Analyze(item.Id)).Finding!;
        Assert.Equal(1, h.Providers.Queries.Count);

        // Unchanged: no second search.
        Assert.Equal("Unchanged", (await h.Analyze(item.Id)).Code);

        // A new prefix rule changes the rules revision and the query, so the item is searched again.
        h.Config.Current.PrefixRules = new[] { new UserPrefixRule { Id = "mysite", Pattern = @"mysite\.example\s*-\s*", Enabled = true, ExampleInput = "mysite.example - X", ExampleOutput = "X" } };
        var second = (await h.Analyze(item.Id)).Finding!;
        Assert.Equal(2, h.Providers.Queries.Count);
        Assert.Equal("Severance", second.SearchTitle);
        Assert.Equal(first.ReviewId, second.ReviewId);

        h.Store.TryUpdateFinding(second.ReviewId, null, x => { x.Status = FindingStatus.Ignored; return true; }, out _);
        Assert.Equal("Skipped:IGNORED", (await h.Analyze(item.Id, force: true)).Code);
    }

    [Fact]
    public async Task Unsupported_server_fails_closed()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var item = h.Library.Add(tv, MediaKind.Series, Reggie);
        var f = (await h.Analyze(item.Id)).Finding!;
        h.Server.ServerVersion = new Version(13, 0, 0);
        var result = await h.Apply.ApplyAsync(new ApplyRequest(f.ReviewId, null, f.Version, ApplyOrigin.Manual, "admin"), CancellationToken.None);
        Assert.Equal("UNSUPPORTED_SERVER", result.Code);
        Assert.Equal(0, h.Library.Writes);
    }

    [Fact]
    public async Task Reconciliation_queues_only_unchecked_or_changed_items_and_prunes_removed()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        var a = h.Library.Add(tv, MediaKind.Series, Reggie);
        var b = h.Library.Add(tv, MediaKind.Series, "/tv/The Office");
        h.Time.Advance(TimeSpan.FromMinutes(10));
        h.Audits.StartAudit(RunKinds.Audit, "admin", false);
        await h.Drain();
        var queries = h.Providers.Queries.Count;

        h.Time.Advance(TimeSpan.FromHours(1));
        var c = h.Library.Add(tv, MediaKind.Series, "/tv/www.UIndex.org - Brand New Show");
        h.Library.Items.TryRemove(b.Id, out _);
        var run = h.Audits.Reconcile("post-scan");
        Assert.Equal(1, run.Enqueued);
        await h.Drain();
        Assert.Equal(queries + 1, h.Providers.Queries.Count);
        Assert.Null(h.Store.GetFindingByItem(b.Id));
        Assert.NotNull(h.Store.GetFindingByItem(c.Id));
        _ = a;
    }

    [Fact]
    public async Task Single_audit_run_lock()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        h.Library.Add(tv, MediaKind.Series, Reggie);
        var one = h.Audits.StartAudit(RunKinds.Audit, "admin", false);
        var two = h.Audits.StartAudit(RunKinds.Audit, "admin", false);
        Assert.Equal(one.RunId, two.RunId);
        await h.Drain();
    }

    [Fact]
    public async Task Search_budget_defers_remaining_audit_items()
    {
        var (h, tv, _) = Setup();
        using var _h = h;
        h.Config.Current.MaxSearchesPerRun = 10;
        for (var i = 0; i < 15; i++)
        {
            h.Library.Add(tv, MediaKind.Series, "/tv/Show Budget " + i);
        }

        var run = h.Audits.StartAudit(RunKinds.Audit, "admin", false);
        await h.Drain();
        Assert.Equal(10, h.Providers.Queries.Count);
        Assert.Equal(5, h.Store.QueueCountForRun(run.RunId));
    }
}
