using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Configuration;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SmartMatch.UnitTests;

public class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "smartmatch-store", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private SmartMatchStore Open()
    {
        var store = new SmartMatchStore(new JsonFileStore(_dir, NullLogger.Instance), NullLogger<SmartMatchStore>.Instance);
        store.Load();
        return store;
    }

    [Fact]
    public async Task Findings_decisions_queue_and_state_survive_restart()
    {
        var store = Open();
        var itemId = Guid.NewGuid();
        var f = store.UpsertFinding(new Finding { ItemId = itemId, OriginalName = "x", Status = FindingStatus.Applied });
        store.AddDecision(new DecisionRecord { DecisionId = Guid.NewGuid(), ReviewId = f.ReviewId, TimeUtc = DateTime.UtcNow, Action = "Apply", Outcome = ApplyOutcome.Applied }, 30, 5000);
        store.Enqueue(Guid.NewGuid(), FindingOrigin.NewScan, null, DateTime.UtcNow, false);
        store.UpdateState(s => s.InitialAuditCompleted = true);
        var salt = store.GetState().PathSalt;
        await store.FlushAsync(CancellationToken.None);

        var reopened = Open();
        Assert.Equal(FindingStatus.Applied, reopened.GetFindingByItem(itemId)!.Status);
        Assert.Single(reopened.GetDecisions(null, 0, 10, out _));
        Assert.Equal(1, reopened.QueueCount());
        Assert.True(reopened.GetState().InitialAuditCompleted);
        Assert.Equal(salt, reopened.GetState().PathSalt);
    }

    [Fact]
    public async Task Corrupt_file_recovers_from_backup()
    {
        var store = Open();
        store.UpsertFinding(new Finding { ItemId = Guid.NewGuid(), OriginalName = "first" });
        await store.FlushAsync(CancellationToken.None);
        store.UpsertFinding(new Finding { ItemId = Guid.NewGuid(), OriginalName = "second" });
        await store.FlushAsync(CancellationToken.None);

        // Truncate the primary file as if power failed mid-write.
        File.WriteAllText(Path.Combine(_dir, "findings.json"), "{\"SchemaVersion\":1,\"Data\":[{\"Revi");
        var reopened = Open();
        Assert.Single(reopened.QueryFindings(_ => true));
        Assert.Contains(reopened.LoadWarnings, w => w.Contains("backup", StringComparison.Ordinal));
    }

    [Fact]
    public void Corrupt_file_and_backup_are_quarantined_without_crashing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "findings.json"), "garbage");
        File.WriteAllText(Path.Combine(_dir, "findings.json.bak"), "garbage");
        var store = Open();
        Assert.Empty(store.QueryFindings(_ => true));
        Assert.Contains(Directory.GetFiles(_dir), p => p.Contains("findings.json.corrupt-", StringComparison.Ordinal));
        Assert.NotEmpty(store.LoadWarnings);
    }

    [Fact]
    public void Newer_schema_is_not_silently_loaded()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "state.json"), "{\"SchemaVersion\":99,\"Data\":{}}");
        var store = Open();
        Assert.Contains(store.LoadWarnings, w => w.Contains("newer", StringComparison.Ordinal));
    }

    [Fact]
    public void Optimistic_concurrency_rejects_stale_versions()
    {
        var store = Open();
        var f = store.UpsertFinding(new Finding { ItemId = Guid.NewGuid() });
        Assert.Equal(UpdateResult.Updated, store.TryUpdateFinding(f.ReviewId, f.Version, x => { x.Reason = "a"; return true; }, out var updated));
        Assert.Equal(f.Version + 1, updated!.Version);
        Assert.Equal(UpdateResult.VersionMismatch, store.TryUpdateFinding(f.ReviewId, f.Version, x => { x.Reason = "b"; return true; }, out _));
    }

    [Fact]
    public void One_finding_per_item_across_repeated_upserts()
    {
        var store = Open();
        var itemId = Guid.NewGuid();
        var a = store.UpsertFinding(new Finding { ItemId = itemId });
        var b = store.UpsertFinding(new Finding { ItemId = itemId });
        Assert.Equal(a.ReviewId, b.ReviewId);
        Assert.Single(store.QueryFindings(_ => true));
    }

    [Fact]
    public void Queue_coalesces_and_debounces_events()
    {
        var store = Open();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        store.Enqueue(id, FindingOrigin.ExistingAudit, Guid.NewGuid(), now, false);
        store.Enqueue(id, FindingOrigin.NewScan, null, now.AddSeconds(30), false);
        store.Enqueue(id, FindingOrigin.NewScan, null, now.AddSeconds(60), false);
        Assert.Equal(1, store.QueueCount());
        Assert.Empty(store.PeekReady(now.AddSeconds(59), 10));
        var ready = store.PeekReady(now.AddSeconds(61), 10).Single();
        Assert.Equal(FindingOrigin.NewScan, ready.Origin);
        Assert.NotNull(ready.RunId);
    }

    [Fact]
    public void Queue_entry_touched_during_processing_is_kept()
    {
        var store = Open();
        var id = Guid.NewGuid();
        store.Enqueue(id, FindingOrigin.NewScan, null, DateTime.UtcNow.AddSeconds(-1), false);
        var taken = store.PeekReady(DateTime.UtcNow, 1).Single();
        Thread.Sleep(5);
        store.Enqueue(id, FindingOrigin.NewScan, null, DateTime.UtcNow, false);
        Assert.False(store.CompleteQueueEntry(id, taken.LastEventUtc));
        Assert.Equal(1, store.QueueCount());
    }

    [Fact]
    public void Retention_keeps_decisions_needed_for_rollback()
    {
        var store = Open();
        var pinnedId = Guid.NewGuid();
        var f = store.UpsertFinding(new Finding { ItemId = Guid.NewGuid(), Status = FindingStatus.Applied, LastDecisionId = pinnedId });
        store.AddDecision(new DecisionRecord { DecisionId = pinnedId, ReviewId = f.ReviewId, TimeUtc = DateTime.UtcNow.AddDays(-400) }, 30, 100);
        for (var i = 0; i < 150; i++)
        {
            store.AddDecision(new DecisionRecord { DecisionId = Guid.NewGuid(), TimeUtc = DateTime.UtcNow.AddMinutes(-i) }, 30, 100);
        }

        store.AddDecision(new DecisionRecord { DecisionId = Guid.NewGuid(), TimeUtc = DateTime.UtcNow.AddDays(-40) }, 30, 100);
        var all = store.GetDecisions(null, 0, 1000, out var total);
        Assert.Equal(101, total);
        Assert.Contains(all, d => d.DecisionId == pinnedId);
    }

    [Fact]
    public void Configuration_validation_rejects_unsafe_values()
    {
        var config = new PluginConfiguration { MinSearchIntervalMs = 10, AutoMinScore = 50, IncludedLibraryIds = new[] { "not-a-guid" } };
        var errors = ConfigurationValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("MinSearchIntervalMs", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("AutoMinScore", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("invalid library id", StringComparison.Ordinal));
        Assert.True(ConfigurationValidator.Sanitize(config));
        Assert.Empty(ConfigurationValidator.Validate(config));
    }

    [Fact]
    public void Defaults_are_safe()
    {
        var config = new PluginConfiguration();
        Assert.Equal(ApplyMode.ReportOnly, config.Mode);
        Assert.Empty(config.AutoApplyLibraryIds);
        Assert.Equal(500, config.MinSearchIntervalMs);
        Assert.True(config.MonitorNewScans);
        Assert.True(config.InitialAuditEnabled);
        Assert.Equal(RefreshProfile.FillMissing, config.RefreshProfile);
        Assert.Empty(ConfigurationValidator.Validate(config));
    }

    [Fact]
    public void User_rule_cannot_be_enabled_without_a_passing_example()
    {
        var config = new PluginConfiguration
        {
            PrefixRules = new[]
            {
                new UserPrefixRule { Id = "a", Pattern = @"mysite\.example\s*-\s*", Enabled = true, ExampleInput = "mysite.example - Title", ExampleOutput = "Wrong" },
            },
        };
        Assert.Contains(ConfigurationValidator.Validate(config), e => e.Contains("not the expected output", StringComparison.Ordinal));
        config.PrefixRules[0].ExampleOutput = "Title";
        Assert.Empty(ConfigurationValidator.Validate(config));
        Assert.Contains(ConfigurationValidator.BuildRuleSet(config).Rules, r => r.Id == "user.a");
    }
}
