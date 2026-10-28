using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Normalization;
using Jellyfin.Plugin.SmartMatch.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartMatch.UnitTests;

public class BudgetTests
{
    private readonly ITestOutputHelper _output;

    public BudgetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Audit_of_1000_records_stays_within_the_memory_budget()
    {
        // PRD §10: no more than 100 MB steady plugin-owned memory growth for 1,000 records.
        using var h = new Harness();
        var tv = h.Library.AddLibrary("TV");
        var movies = h.Library.AddLibrary("Movies", "movies");
        for (var i = 0; i < 1000; i++)
        {
            var title = "Synthetic Title " + i;
            if (i % 2 == 0)
            {
                h.Providers.Add(MediaKind.Series, title, 2000 + (i % 25), (100000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture));
                h.Library.Add(tv, MediaKind.Series, "/tv/www.UIndex.org - " + title + " (" + (2000 + (i % 25)) + ")");
            }
            else
            {
                h.Providers.Add(MediaKind.Movie, title, 2000 + (i % 25), (100000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture));
                h.Library.Add(movies, MediaKind.Movie, "/movies/" + title.Replace(' ', '.') + "." + (2000 + (i % 25)) + ".1080p.BluRay.x264/" + title.Replace(' ', '.') + ".mkv");
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        var started = DateTime.UtcNow;
        h.Audits.StartAudit(RunKinds.Audit, "test", false);
        await h.Drain();
        await h.Store.FlushAsync(default);
        var elapsed = DateTime.UtcNow - started;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(true);
        var growthMb = (after - before) / (1024.0 * 1024.0);
        var journalKb = new System.IO.DirectoryInfo(h.Directory).GetFiles("*.json").Sum(f => f.Length) / 1024.0;
        _output.WriteLine($"findings={h.Store.QueryFindings(_ => true).Count} growth={growthMb:0.0} MB journal={journalKb:0} KB elapsed={elapsed.TotalSeconds:0.0}s");

        Assert.Equal(1000, h.Store.QueryFindings(_ => true).Count);
        Assert.True(growthMb < 100, $"steady growth {growthMb:0.0} MB");
    }
}
