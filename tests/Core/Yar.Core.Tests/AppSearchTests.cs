namespace Yar.Core.Tests;

public class AppSearchTests
{
    private static AppInfo App(string name, string? summary, params AppCategory[] categories) => new(
        "packagekit", name, name, summary, null, null, null, null, null, null, [], null,
        new Dictionary<string, string>())
    {
        Categories = categories,
    };

    private static string Name(AppCategory category) => category switch
    {
        AppCategory.Games => "Games",
        AppCategory.SoundAndVideo => "Sound & Video",
        AppCategory.Utilities => "Utilities",
        _ => category.ToString(),
    };

    private static SearchMatch Match(AppInfo app, string query) => AppSearch.Match(app, query, Name);

    [Theory]
    [InlineData("games")]
    [InlineData("GAME")]
    [InlineData("  gam ")]
    public void Finds_apps_by_their_tag(string query)
    {
        var match = Match(App("SuperTux", "Jump and run", AppCategory.Games), query);

        Assert.Equal(new SearchMatch(SearchMatchKind.Tag, AppCategory.Games), match);
    }

    [Fact]
    public void Reports_which_of_several_tags_matched()
    {
        var app = App("VLC", "Media player", AppCategory.SoundAndVideo, AppCategory.Utilities);

        Assert.Equal(AppCategory.Utilities, Match(app, "util").Category);
        Assert.Equal(AppCategory.SoundAndVideo, Match(app, "video").Category);
    }

    [Fact]
    public void A_word_match_wins_over_a_tag_match()
    {
        var byName = Match(App("Games Launcher", null, AppCategory.Games), "games");
        var bySummary = Match(App("Lutris", "Play all your games", AppCategory.Games), "games");

        Assert.Equal(SearchMatchKind.Word, byName.Kind);
        Assert.Null(byName.Category);
        Assert.Equal(SearchMatchKind.Word, bySummary.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("g")]
    [InlineData("office")]
    [InlineData("other")]
    public void No_match(string query)
    {
        Assert.Equal(SearchMatch.None, Match(App("SuperTux", "Jump and run", AppCategory.Games, AppCategory.Other), query));
    }

    [Fact]
    public void Apps_without_a_summary_are_searched_by_name()
    {
        Assert.Equal(SearchMatchKind.Word, Match(App("Krita", null), "kri").Kind);
        Assert.Equal(SearchMatchKind.None, Match(App("Krita", null), "paint").Kind);
    }
}
