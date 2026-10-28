using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.SmartMatch.Configuration;
using Jellyfin.Plugin.SmartMatch.Normalization;
using Xunit;

namespace SmartMatch.UnitTests;

public class NormalizerTests
{
    private static readonly MediaNameNormalizer Normalizer = new(() => 2028);

    // Name, source, expected search title, expected year, expected episode context.
    public static IEnumerable<object?[]> Fixtures => new List<object?[]>
    {
        // Required fixtures (TECHNICAL_DESIGN.md §6).
        new object?[] { "www.UIndex.org - The Fall and Rise of Reggie Dinkins", NameSourceKind.SeriesFolder, "The Fall and Rise of Reggie Dinkins", null, null },
        new object?[] { "www.UIndex.org - The.Fall.and.Rise.of.Reggie.Dinkins.S01E01.1080p.WEB-DL.mkv", NameSourceKind.EpisodeFile, "The Fall and Rise of Reggie Dinkins", null, "S01E01" },
        new object?[] { "The.Dark.Knight.2008.1080p.BluRay.x264.mkv", NameSourceKind.MovieFile, "The Dark Knight", 2008, null },
        new object?[] { "Dune.1984.1080p.mkv", NameSourceKind.MovieFile, "Dune", 1984, null },
        new object?[] { "Dune.2021.2160p.HEVC.mkv", NameSourceKind.MovieFile, "Dune", 2021, null },
        new object?[] { "The.Office.S03E05.720p.WEB-DL.mkv", NameSourceKind.EpisodeFile, "The Office", null, "S03E05" },
        new object?[] { "Web.2013.mkv", NameSourceKind.MovieFile, "Web", 2013, null },
        new object?[] { "Complete.Unknown.2024.mkv", NameSourceKind.MovieFile, "Complete Unknown", 2024, null },
        new object?[] { "Mr.Robot.S01E01.mkv", NameSourceKind.EpisodeFile, "Mr. Robot", null, "S01E01" },

        // PRD edge cases.
        new object?[] { "Web (2013)", NameSourceKind.MovieFolder, "Web", 2013, null },
        new object?[] { "Complete Unknown (2024)", NameSourceKind.MovieFolder, "Complete Unknown", 2024, null },
        new object?[] { "Doctor Who (1963)", NameSourceKind.SeriesFolder, "Doctor Who", 1963, null },
        new object?[] { "Doctor Who (2005)", NameSourceKind.SeriesFolder, "Doctor Who", 2005, null },
        new object?[] { "Dark.S01E01.mkv", NameSourceKind.EpisodeFile, "Dark", null, "S01E01" },
        new object?[] { "Movie.Title.2024.1080p.HEVC.mkv", NameSourceKind.MovieFile, "Movie Title", 2024, null },
        new object?[] { "Wall-E (2008)", NameSourceKind.MovieFolder, "Wall-E", 2008, null },
        new object?[] { "Spider-Man (2002)", NameSourceKind.MovieFolder, "Spider-Man", 2002, null },
        new object?[] { "Spider-Man.2002.1080p.BluRay.mkv", NameSourceKind.MovieFile, "Spider-Man", 2002, null },

        // Site watermarks.
        new object?[] { "www.UIndex.org - Some.Movie.2019.720p.WEBRip.x264.mkv", NameSourceKind.MovieFile, "Some Movie", 2019, null },
        new object?[] { "www.UIndex.org.Another.Show.S02E03.mkv", NameSourceKind.EpisodeFile, "Another Show", null, "S02E03" },
        new object?[] { "[www.UIndex.org] Show Title", NameSourceKind.SeriesFolder, "Show Title", null, null },
        new object?[] { "UIndex.org - Show Title", NameSourceKind.SeriesFolder, "Show Title", null, null },
        new object?[] { "[ www.Torrenting.com ] - Movie.Title.2024.1080p.mkv", NameSourceKind.MovieFile, "Movie Title", 2024, null },
        new object?[] { "www.ExampleSite.com - Some Title", NameSourceKind.SeriesFolder, "Some Title", null, null },
        new object?[] { "[ExampleSite.org] Some Title (2010)", NameSourceKind.MovieFolder, "Some Title", 2010, null },
        new object?[] { "[YTS.MX] Movie Name (2020)", NameSourceKind.MovieFolder, "Movie Name", 2020, null },
        new object?[] { "【www.example.cc】Film Name 2015 1080p", NameSourceKind.MovieFolder, "Film Name", 2015, null },

        // Legitimate dots and domains in titles survive.
        new object?[] { "Hackers.com.2009.mkv", NameSourceKind.MovieFile, "Hackers com", 2009, null },
        new object?[] { "S.W.A.T.2017.S01E01.1080p.mkv", NameSourceKind.EpisodeFile, "S.W.A.T.", 2017, "S01E01" },
        new object?[] { "Marvels.Agents.of.S.H.I.E.L.D.S01E01.mkv", NameSourceKind.EpisodeFile, "Marvels Agents of S.H.I.E.L.D.", null, "S01E01" },
        new object?[] { "The.Man.from.U.N.C.L.E.2015.1080p.mkv", NameSourceKind.MovieFile, "The Man from U.N.C.L.E.", 2015, null },
        new object?[] { "A.I.Artificial.Intelligence.2001.mkv", NameSourceKind.MovieFile, "A.I. Artificial Intelligence", 2001, null },
        new object?[] { "Dr.No.1962.720p.mkv", NameSourceKind.MovieFile, "Dr. No", 1962, null },
        new object?[] { "Tron.2.0.2011.mkv", NameSourceKind.MovieFile, "Tron 2.0", 2011, null },

        // Numeric titles.
        new object?[] { "1917.2019.1080p.BluRay.mkv", NameSourceKind.MovieFile, "1917", 2019, null },
        new object?[] { "1917 (2019)", NameSourceKind.MovieFolder, "1917", 2019, null },
        new object?[] { "2012 (2009)", NameSourceKind.MovieFolder, "2012", 2009, null },
        new object?[] { "Blade Runner 2049 (2017)", NameSourceKind.MovieFolder, "Blade Runner 2049", 2017, null },
        new object?[] { "2001 A Space Odyssey (1968)", NameSourceKind.MovieFolder, "2001 A Space Odyssey", 1968, null },
        new object?[] { "Apollo.13.1995.1080p.mkv", NameSourceKind.MovieFile, "Apollo 13", 1995, null },

        // Ordinary words are never removed from inside a title.
        new object?[] { "Up (2009)", NameSourceKind.MovieFolder, "Up", 2009, null },
        new object?[] { "It (2017)", NameSourceKind.MovieFolder, "It", 2017, null },
        new object?[] { "The Complete Works", NameSourceKind.SeriesFolder, "The Complete Works", null, null },
        new object?[] { "Theatrical Lives (2016)", NameSourceKind.MovieFolder, "Theatrical Lives", 2016, null },
        new object?[] { "Web.Of.Lies.S01E01.720p.HDTV.mkv", NameSourceKind.EpisodeFile, "Web Of Lies", null, "S01E01" },
        new object?[] { "Aliens.Extended.1986.1080p.BluRay.mkv", NameSourceKind.MovieFile, "Aliens", 1986, null },
        new object?[] { "Show.Name.S01.COMPLETE.1080p.WEB-DL", NameSourceKind.SeriesFolder, "Show Name", null, "S01" },

        // Apostrophes, accents, Unicode, emoji.
        new object?[] { "Ocean's.Eleven.2001.720p.mkv", NameSourceKind.MovieFile, "Ocean's Eleven", 2001, null },
        new object?[] { "Schindler's List (1993)", NameSourceKind.MovieFolder, "Schindler's List", 1993, null },
        new object?[] { "Amélie (2001)", NameSourceKind.MovieFolder, "Amélie", 2001, null },
        new object?[] { "La.Casa.de.Papel.S01E01.1080p.mkv", NameSourceKind.EpisodeFile, "La Casa de Papel", null, "S01E01" },
        new object?[] { "千と千尋の神隠し (2001)", NameSourceKind.MovieFolder, "千と千尋の神隠し", 2001, null },
        new object?[] { "Ein.Fall.für.zwei.S01E02.mkv", NameSourceKind.EpisodeFile, "Ein Fall für zwei", null, "S01E02" },
        new object?[] { "🎬 Party Show", NameSourceKind.SeriesFolder, "🎬 Party Show", null, null },

        // Episode formats.
        new object?[] { "Show.Name.1x02.HDTV.mkv", NameSourceKind.EpisodeFile, "Show Name", null, "1x02" },
        new object?[] { "Show Name - S02E10 - Episode Title.mkv", NameSourceKind.EpisodeFile, "Show Name", null, "S02E10" },
        new object?[] { "The.Daily.Show.2024.01.15.Guest.720p.mkv", NameSourceKind.EpisodeFile, "The Daily Show", null, "2024-01-15" },
        new object?[] { "Show Name Season 1", NameSourceKind.SeriesFolder, "Show Name", null, "Season 1" },
        new object?[] { "Show.Name.S01-S03.1080p.BluRay", NameSourceKind.SeriesFolder, "Show Name", null, "S01-S03" },
        new object?[] { "Doctor.Who.2005.S01E01.mkv", NameSourceKind.EpisodeFile, "Doctor Who", 2005, "S01E01" },

        // Release tags, groups and brackets.
        new object?[] { "Movie Title (2020) [1080p] [YTS.AM]", NameSourceKind.MovieFolder, "Movie Title", 2020, null },
        new object?[] { "Movie_Title_2018_720p_x265-GROUP.mkv", NameSourceKind.MovieFile, "Movie Title", 2018, null },
        new object?[] { "Some.Film.2016.REPACK.1080p.BluRay.DTS-HD.MA.5.1.x264-GRP.mkv", NameSourceKind.MovieFile, "Some Film", 2016, null },
        new object?[] { "Another Film [2160p] [HDR10]", NameSourceKind.MovieFolder, "Another Film", null, null },
        new object?[] { "Film.Without.Year.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", NameSourceKind.MovieFile, "Film Without Year", null, null },

        // Country qualifier is evidence, not part of the query.
        new object?[] { "The.Office.US.S01E01.1080p.WEB-DL.mkv", NameSourceKind.EpisodeFile, "The Office", null, "S01E01" },

        // Provider id markers are kept as evidence.
        new object?[] { "Movie Title (2020) [tmdbid-12345]", NameSourceKind.MovieFolder, "Movie Title", 2020, null },

        // Already clean titles stay unchanged.
        new object?[] { "The Fall and Rise of Reggie Dinkins", NameSourceKind.SeriesFolder, "The Fall and Rise of Reggie Dinkins", null, null },
        new object?[] { "Mr. Robot", NameSourceKind.SeriesFolder, "Mr. Robot", null, null },
        new object?[] { "Brooklyn Nine-Nine", NameSourceKind.SeriesFolder, "Brooklyn Nine-Nine", null, null },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Normalizes_fixture(string input, NameSourceKind source, string expectedTitle, int? expectedYear, string? expectedEpisode)
    {
        var kind = source is NameSourceKind.SeriesFolder or NameSourceKind.EpisodeFile ? MediaKind.Series : MediaKind.Movie;
        var result = Normalizer.Normalize(new MediaNameInput(input, source, kind), RuleSet.Default);

        Assert.True(result.IsSafe, string.Join("; ", result.Warnings));
        Assert.Equal(expectedTitle, result.SearchTitle);
        Assert.Equal(expectedYear, result.TrustedYear);
        Assert.Equal(expectedEpisode, result.EpisodeContext);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Normalization_is_idempotent(string input, NameSourceKind source, string expectedTitle, int? expectedYear, string? expectedEpisode)
    {
        _ = expectedYear;
        _ = expectedEpisode;
        _ = input;
        var kind = source is NameSourceKind.SeriesFolder or NameSourceKind.EpisodeFile ? MediaKind.Series : MediaKind.Movie;
        var again = Normalizer.Normalize(new MediaNameInput(expectedTitle, NameSourceKind.Preview, kind), RuleSet.Default);
        Assert.Equal(expectedTitle, again.SearchTitle);
    }

    [Fact]
    public void Fixture_corpus_meets_the_95_percent_clean_title_goal()
    {
        // G1: at least 40 problematic names, 95% produce the expected clean title.
        var fixtures = Fixtures.ToList();
        Assert.True(fixtures.Count >= 40);
        var passed = fixtures.Count(f =>
        {
            var source = (NameSourceKind)f[1]!;
            var kind = source is NameSourceKind.SeriesFolder or NameSourceKind.EpisodeFile ? MediaKind.Series : MediaKind.Movie;
            return Normalizer.Normalize(new MediaNameInput((string)f[0]!, source, kind), RuleSet.Default).SearchTitle == (string)f[2]!;
        });
        Assert.True(passed >= Math.Ceiling(fixtures.Count * 0.95), $"{passed}/{fixtures.Count}");
    }

    [Fact]
    public void Uindex_trace_shows_prefix_and_separator()
    {
        var result = Normalizer.Normalize(new MediaNameInput("www.UIndex.org - The Fall and Rise of Reggie Dinkins", NameSourceKind.SeriesFolder, MediaKind.Series), RuleSet.Default);
        var trace = result.Steps.Select(s => s.ToString()).ToList();
        Assert.Contains("removed site-prefix (site.uindex): www.UIndex.org", trace);
        Assert.Contains("trimmed separator: -", trace);
        Assert.Empty(result.Warnings);
        Assert.Equal("the fall and rise of reggie dinkins", result.ComparisonKey);
    }

    [Fact]
    public void Folder_names_never_lose_a_dotted_word_as_extension()
    {
        var result = Normalizer.Normalize(new MediaNameInput("Mr.Robot", NameSourceKind.SeriesFolder, MediaKind.Series), RuleSet.Default);
        Assert.Equal("Mr. Robot", result.SearchTitle);
    }

    [Fact]
    public void Country_qualifier_is_a_warning()
    {
        var result = Normalizer.Normalize(new MediaNameInput("The.Office.US.S01E01.mkv", NameSourceKind.EpisodeFile, MediaKind.Series), RuleSet.Default);
        Assert.Contains(result.Warnings, w => w.Contains("country qualifier", StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_marker_is_recorded()
    {
        var result = Normalizer.Normalize(new MediaNameInput("Movie Title (2020) [tmdbid-12345]", NameSourceKind.MovieFolder, MediaKind.Movie), RuleSet.Default);
        Assert.Equal("12345", result.ProviderHints["Tmdb"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1080p")]
    [InlineData("- . _")]
    [InlineData("[1080p]")]
    public void Unusable_names_are_unsafe(string input)
    {
        var result = Normalizer.Normalize(new MediaNameInput(input, NameSourceKind.SeriesFolder, MediaKind.Series), RuleSet.Default);
        Assert.False(result.IsSafe);
        Assert.Equal(string.Empty, result.SearchTitle);
    }

    [Fact]
    public void Comparison_key_folds_case_punctuation_and_accents()
    {
        Assert.Equal("mr robot", MediaNameNormalizer.ComparisonKey("Mr. Robot"));
        Assert.Equal("mr robot", MediaNameNormalizer.ComparisonKey("mr robot"));
        Assert.Equal("amelie", MediaNameNormalizer.ComparisonKey("Amélie"));
        Assert.Equal("law and order", MediaNameNormalizer.ComparisonKey("Law & Order"));
        Assert.Equal("schindlers list", MediaNameNormalizer.ComparisonKey("Schindler’s List"));
    }

    [Fact]
    public void Random_inputs_are_idempotent_bounded_and_never_throw()
    {
        // Property-style test with a fixed seed so failures are reproducible.
        var seed = int.TryParse(Environment.GetEnvironmentVariable("SMARTMATCH_FUZZ_SEED"), out var s) ? s : 20261028;
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("SMARTMATCH_FUZZ_ITERATIONS"), out var n0) ? n0 : 3000;
        var random = new Random(seed);
        string[] words = { "The", "Show", "www.UIndex.org", "-", "S01E02", "1080p", "WEB-DL", "x265", "2019", "(2020)", "[YTS.MX]", "Mr.", "Amélie", "Web", "Complete", "It", "Up", "Dr", "A.I.", "Ocean's", "S.H.I.E.L.D", "2.0", "1917", "_", ".", "🎬", "tvdbid-12", "Season", "3", "US", "Extended" };
        char[] separators = { ' ', '.', '_', '-' };
        for (var i = 0; i < iterations; i++)
        {
            var sb = new StringBuilder();
            var n = random.Next(1, 9);
            for (var j = 0; j < n; j++)
            {
                if (j > 0)
                {
                    sb.Append(separators[random.Next(separators.Length)]);
                }

                sb.Append(words[random.Next(words.Length)]);
            }

            var input = sb.ToString();
            foreach (var source in new[] { NameSourceKind.SeriesFolder, NameSourceKind.MovieFile })
            {
                var kind = source == NameSourceKind.SeriesFolder ? MediaKind.Series : MediaKind.Movie;
                var first = Normalizer.Normalize(new MediaNameInput(input, source, kind), RuleSet.Default);
                Assert.True(first.SearchTitle.Length <= MediaNameNormalizer.MaxInputLength);
                if (!first.IsSafe)
                {
                    continue;
                }

                var second = Normalizer.Normalize(new MediaNameInput(first.SearchTitle, NameSourceKind.Preview, kind), RuleSet.Default);
                Assert.True(first.SearchTitle == second.SearchTitle, $"Not idempotent: '{input}' -> '{first.SearchTitle}' -> '{second.SearchTitle}'");
            }
        }
    }

    [Fact]
    public void Very_long_input_is_truncated_with_warning()
    {
        var result = Normalizer.Normalize(new MediaNameInput(new string('a', 5000), NameSourceKind.SeriesFolder, MediaKind.Series), RuleSet.Default);
        Assert.True(result.SearchTitle.Length <= MediaNameNormalizer.MaxInputLength);
        Assert.Contains(result.Warnings, w => w.Contains("truncated", StringComparison.Ordinal));
    }

    [Fact]
    public void User_prefix_rule_is_anchored_and_applied()
    {
        var rules = RuleSet.Create(new[] { new PrefixRule("user.mysite", "test", @"mysite\.example\s*-\s*", "mysite.example - Title", "Title", false) });
        var result = Normalizer.Normalize(new MediaNameInput("mysite.example - Great Show", NameSourceKind.SeriesFolder, MediaKind.Series), rules);
        Assert.Equal("Great Show", result.SearchTitle);

        // Anchored: the same text in the middle is untouched.
        var middle = Normalizer.Normalize(new MediaNameInput("Great mysite.example - Show", NameSourceKind.SeriesFolder, MediaKind.Series), rules);
        Assert.Equal("Great mysite example - Show", middle.SearchTitle);
    }

    [Fact]
    public void Catastrophic_user_pattern_times_out_instead_of_hanging()
    {
        var rules = RuleSet.Create(new[] { new PrefixRule("user.evil", "evil", "(a+)+b", "aab", "", false) });
        var started = DateTime.UtcNow;
        var result = Normalizer.Normalize(new MediaNameInput(new string('a', 40) + "!", NameSourceKind.SeriesFolder, MediaKind.Series), rules);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Contains(result.Warnings, w => w.Contains("timed out", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("(", false)]
    [InlineData(".*", false)]
    [InlineData(@"site\.example\s*-", true)]
    public void Rule_validation(string pattern, bool valid)
    {
        Assert.Equal(valid, RuleSet.TryCompile(pattern, out _, out _));
    }

    [Fact]
    public void Built_in_rule_examples_hold()
    {
        foreach (var rule in BuiltInPrefixRules.All)
        {
            Assert.Equal(rule.ExampleOutput, ConfigurationValidator.PreviewRule(rule.Pattern, rule.Example, builtIn: true));
        }
    }

    [Fact]
    public void Rule_revision_changes_with_rules()
    {
        var a = RuleSet.Default.Revision;
        var b = RuleSet.Create(new[] { new PrefixRule("user.x", "x", "x-", "x-y", "y", false) }).Revision;
        Assert.NotEqual(a, b);
    }
}
