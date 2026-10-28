using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Normalization;
using Xunit;
using Xunit.Abstractions;

namespace SmartMatch.UnitTests;

/// <summary>
/// Labeled acceptance corpus (PRD G1/G2/M3 exit gate). Provider identifiers are synthetic; the
/// catalogue deliberately contains remakes, sequels, near-homonyms and same-title series so the
/// automatic gate is exercised against realistic confusers.
/// </summary>
public class CorpusTests
{
    private readonly ITestOutputHelper _output;

    public CorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Catalogue: kind, name, year, id.
    private static readonly (MediaKind Kind, string Name, int Year, string Id)[] Catalogue =
    {
        (MediaKind.Series, "The Fall and Rise of Reggie Dinkins", 2026, "1001"),
        (MediaKind.Series, "The Office", 2005, "1002"),
        (MediaKind.Series, "The Office", 2001, "1003"),
        (MediaKind.Series, "Doctor Who", 1963, "1004"),
        (MediaKind.Series, "Doctor Who", 2005, "1005"),
        (MediaKind.Series, "Dark", 2017, "1006"),
        (MediaKind.Series, "Dark Matter", 2015, "1007"),
        (MediaKind.Series, "Dark Matter", 2024, "1008"),
        (MediaKind.Series, "Mr. Robot", 2015, "1009"),
        (MediaKind.Series, "Severance", 2022, "1010"),
        (MediaKind.Series, "Shogun", 2024, "1011"),
        (MediaKind.Series, "Shogun", 1980, "1012"),
        (MediaKind.Series, "Brooklyn Nine-Nine", 2013, "1013"),
        (MediaKind.Series, "Marvel's Agents of S.H.I.E.L.D.", 2013, "1014"),
        (MediaKind.Series, "La Casa de Papel", 2017, "1015"),
        (MediaKind.Series, "Slow Horses", 2022, "1016"),
        (MediaKind.Series, "The Bear", 2022, "1017"),
        (MediaKind.Series, "Ted Lasso", 2020, "1018"),
        (MediaKind.Series, "House of Cards", 2013, "1019"),
        (MediaKind.Series, "House of Cards", 1990, "1020"),
        (MediaKind.Series, "Fargo", 2014, "1021"),
        (MediaKind.Series, "Ein Fall für zwei", 1981, "1022"),
        (MediaKind.Movie, "The Dark Knight", 2008, "2001"),
        (MediaKind.Movie, "The Dark Knight Rises", 2012, "2002"),
        (MediaKind.Movie, "Dune", 1984, "2003"),
        (MediaKind.Movie, "Dune", 2021, "2004"),
        (MediaKind.Movie, "Dune: Part Two", 2024, "2005"),
        (MediaKind.Movie, "Web", 2013, "2006"),
        (MediaKind.Movie, "A Complete Unknown", 2024, "2007"),
        (MediaKind.Movie, "Complete Unknown", 1996, "2008"),
        (MediaKind.Movie, "1917", 2019, "2009"),
        (MediaKind.Movie, "Blade Runner", 1982, "2010"),
        (MediaKind.Movie, "Blade Runner 2049", 2017, "2011"),
        (MediaKind.Movie, "Spider-Man", 2002, "2012"),
        (MediaKind.Movie, "Spider-Man 2", 2004, "2013"),
        (MediaKind.Movie, "Amélie", 2001, "2014"),
        (MediaKind.Movie, "Ocean's Eleven", 2001, "2015"),
        (MediaKind.Movie, "Ocean's Eleven", 1960, "2016"),
        (MediaKind.Movie, "Up", 2009, "2017"),
        (MediaKind.Movie, "It", 2017, "2018"),
        (MediaKind.Movie, "It", 1990, "2019"),
        (MediaKind.Movie, "Arrival", 2016, "2020"),
        (MediaKind.Movie, "The Man from U.N.C.L.E.", 2015, "2021"),
        (MediaKind.Movie, "Halloween", 1978, "2022"),
        (MediaKind.Movie, "Halloween", 2018, "2023"),
        (MediaKind.Movie, "Halloween", 2007, "2024"),
        (MediaKind.Movie, "Oppenheimer", 2023, "2025"),
        (MediaKind.Movie, "Parasite", 2019, "2026"),
        (MediaKind.Movie, "Wall-E", 2008, "2027"),
        (MediaKind.Movie, "Apollo 13", 1995, "2028"),
    };

    // Path, kind, expected id (null = no correct automatic identity exists / ambiguous by design).
    private static readonly (string Path, MediaKind Kind, string? Expected, bool Ambiguous)[] Corpus =
    {
        ("/tv/www.UIndex.org - The Fall and Rise of Reggie Dinkins", MediaKind.Series, "1001", false),
        ("/tv/www.UIndex.org - The.Fall.and.Rise.of.Reggie.Dinkins.S01.1080p.WEB-DL", MediaKind.Series, "1001", false),
        ("/tv/The Office", MediaKind.Series, null, true),
        ("/tv/The.Office.US.S01.1080p.WEB-DL", MediaKind.Series, null, true),
        ("/tv/The Office (2005)", MediaKind.Series, "1002", false),
        ("/tv/Doctor Who", MediaKind.Series, null, true),
        ("/tv/Doctor Who (2005)", MediaKind.Series, "1005", false),
        ("/tv/Doctor.Who.1963.S01.DVDRip", MediaKind.Series, "1004", false),
        ("/tv/Dark (2017)", MediaKind.Series, "1006", false),
        ("/tv/www.UIndex.org - Dark Matter (2024)", MediaKind.Series, "1008", false),
        ("/tv/Dark Matter", MediaKind.Series, null, true),
        ("/tv/Mr.Robot.S01-S04.1080p.BluRay", MediaKind.Series, "1009", false),
        ("/tv/[www.UIndex.org] Severance (2022)", MediaKind.Series, "1010", false),
        ("/tv/Shogun.2024.S01.2160p.WEB-DL", MediaKind.Series, "1011", false),
        ("/tv/Shogun", MediaKind.Series, null, true),
        ("/tv/Brooklyn.Nine-Nine.S01.720p.HDTV", MediaKind.Series, "1013", false),
        ("/tv/La.Casa.de.Papel.2017.S01.1080p", MediaKind.Series, "1015", false),
        ("/tv/www.UIndex.org - Slow.Horses.S04.1080p.ATVP.WEB-DL", MediaKind.Series, "1016", false),
        ("/tv/The Bear (2022)", MediaKind.Series, "1017", false),
        ("/tv/Ted.Lasso.S01.COMPLETE.1080p", MediaKind.Series, "1018", false),
        ("/tv/House of Cards", MediaKind.Series, null, true),
        ("/tv/House.of.Cards.2013.S01.1080p", MediaKind.Series, "1019", false),
        ("/tv/Fargo (2014)", MediaKind.Series, "1021", false),
        ("/tv/Ein.Fall.für.zwei.S01", MediaKind.Series, "1022", false),
        ("/movies/The.Dark.Knight.2008.1080p.BluRay.x264/The.Dark.Knight.2008.1080p.BluRay.x264.mkv", MediaKind.Movie, "2001", false),
        ("/movies/Dune (1984)/Dune.1984.1080p.mkv", MediaKind.Movie, "2003", false),
        ("/movies/Dune (2021)/Dune.2021.2160p.HEVC.mkv", MediaKind.Movie, "2004", false),
        ("/movies/Dune/Dune.mkv", MediaKind.Movie, null, true),
        ("/movies/Web (2013)/Web.2013.mkv", MediaKind.Movie, "2006", false),
        ("/movies/Complete.Unknown.1996.DVDRip/Complete.Unknown.1996.DVDRip.mkv", MediaKind.Movie, "2008", false),
        ("/movies/1917 (2019)/1917.2019.1080p.BluRay.mkv", MediaKind.Movie, "2009", false),
        ("/movies/Blade Runner 2049 (2017)/Blade.Runner.2049.2017.2160p.mkv", MediaKind.Movie, "2011", false),
        ("/movies/Spider-Man (2002)/Spider-Man.2002.1080p.BluRay.mkv", MediaKind.Movie, "2012", false),
        ("/movies/Amelie (2001)/Amelie.2001.1080p.mkv", MediaKind.Movie, "2014", false),
        ("/movies/www.UIndex.org - Oceans.Eleven.2001.720p/Oceans.Eleven.2001.720p.mkv", MediaKind.Movie, "2015", false),
        ("/movies/Up (2009)/Up.2009.1080p.mkv", MediaKind.Movie, "2017", false),
        ("/movies/It (2017)/It.2017.1080p.mkv", MediaKind.Movie, "2018", false),
        ("/movies/It/It.mkv", MediaKind.Movie, null, true),
        ("/movies/[ www.Torrenting.com ] - Arrival.2016.1080p.BluRay/Arrival.2016.1080p.BluRay.mkv", MediaKind.Movie, "2020", false),
        ("/movies/The.Man.from.U.N.C.L.E.2015.1080p/The.Man.from.U.N.C.L.E.2015.1080p.mkv", MediaKind.Movie, "2021", false),
        ("/movies/Halloween (2018)/Halloween.2018.mkv", MediaKind.Movie, "2023", false),
        ("/movies/Halloween/Halloween.mkv", MediaKind.Movie, null, true),
        ("/movies/Oppenheimer.2023.IMAX.2160p.WEB-DL/Oppenheimer.2023.IMAX.2160p.WEB-DL.mkv", MediaKind.Movie, "2025", false),
        ("/movies/www.UIndex.org - Parasite (2019)/Parasite.2019.mkv", MediaKind.Movie, "2026", false),
        ("/movies/Wall-E (2008)/Wall-E.2008.mkv", MediaKind.Movie, "2027", false),
        ("/movies/Apollo.13.1995.1080p/Apollo.13.1995.1080p.mkv", MediaKind.Movie, "2028", false),
        ("/movies/Totally Unknown Indie (2011)/Totally.Unknown.Indie.2011.mkv", MediaKind.Movie, null, false),
    };

    [Fact]
    public async Task Labeled_corpus_has_no_wrong_automatic_application_and_ranks_correctly()
    {
        using var h = new Harness();
        var tv = h.Library.AddLibrary("TV");
        var movies = h.Library.AddLibrary("Movies", "movies");
        foreach (var c in Catalogue)
        {
            h.Providers.Add(c.Kind, c.Name, c.Year, c.Id);
        }

        h.Config.Current.Mode = Jellyfin.Plugin.SmartMatch.Configuration.ApplyMode.AutoApplyHighConfidence;
        h.Config.Current.AutoApplyLibraryIds = new[] { tv.ToString(), movies.ToString() };

        var items = Corpus.Select(c => (Case: c, Item: h.Library.Add(c.Kind == MediaKind.Series ? tv : movies, c.Kind, c.Path))).ToList();
        h.Audits.StartAudit(Jellyfin.Plugin.SmartMatch.Services.RunKinds.Audit, "test", false);
        await h.Drain();

        var wrongAuto = new List<string>();
        var correctTop = 0;
        var unambiguousWithResult = 0;
        var autoApplied = 0;
        foreach (var (c, item) in items)
        {
            var f = h.FindingFor(item.Id);
            var applied = item.ProviderIds.TryGetValue("Tmdb", out var id) ? id : null;
            _output.WriteLine($"{f.Status,-12} {f.SearchTitle,-40} year={f.TrustedYear} top={f.Recommended()?.CandidateId} applied={applied}");
            if (applied is not null)
            {
                autoApplied++;
                if (applied != c.Expected)
                {
                    wrongAuto.Add(c.Path + " -> " + applied);
                }
            }

            if (c.Ambiguous)
            {
                Assert.Null(applied);
                Assert.NotEqual(FindingStatus.Proposed, f.Status);
            }

            if (!c.Ambiguous && c.Expected is not null)
            {
                unambiguousWithResult++;
                if (f.Recommended()?.CandidateId == "tmdb:" + c.Expected || f.AppliedCandidateId == "tmdb:" + c.Expected)
                {
                    correctTop++;
                }
            }
        }

        _output.WriteLine($"auto-applied={autoApplied} correctTop={correctTop}/{unambiguousWithResult}");
        Assert.True(Corpus.Length >= 40);
        Assert.Empty(wrongAuto);
        Assert.True(correctTop >= Math.Ceiling(unambiguousWithResult * 0.9), $"{correctTop}/{unambiguousWithResult}");
        Assert.True(autoApplied > 0);
    }
}
