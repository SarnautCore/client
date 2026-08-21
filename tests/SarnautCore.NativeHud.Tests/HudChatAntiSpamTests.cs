using Xunit;

namespace SarnautCore.NativeHud.Tests;

public sealed class HudChatAntiSpamTests
{
    [Fact]
    public void RecordingFixtureMatchesRetailFilterAggregation()
    {
        HudChatAntiSpamCatalog catalog = Catalog();

        Assert.Equal(100, catalog.Score(HudChatChannel.Say, string.Empty, "Sender", []));
        Assert.Equal(0, catalog.Score(HudChatChannel.Say, "   ", "Sender", []));
        Assert.Equal(250, catalog.Score(HudChatChannel.Say, "SELL", "Sender", []));
        Assert.Equal(250, catalog.Score(HudChatChannel.Say, "!!!", "Sender", []));
        Assert.Equal(200, catalog.Score(HudChatChannel.Say, "cheap gold here", "Sender", []));
        Assert.Equal(200, catalog.Score(HudChatChannel.Say, "gold and gold", "Sender", []));
    }

    [Fact]
    public void ChannelAndExactFriendGatesRunBeforeScoring()
    {
        HudChatAntiSpamCatalog catalog = Catalog();

        Assert.Equal(0, catalog.Score(HudChatChannel.World, "SELL GOLD!!!", "Sender", []));
        Assert.Equal(0, catalog.Score(HudChatChannel.ZoneSpecial, "SELL GOLD!!!", "Sender", []));
        Assert.Equal(0, catalog.Score(HudChatChannel.Say, "SELL GOLD!!!", "Sender", ["Sender"]));
        Assert.NotEqual(0, catalog.Score(HudChatChannel.Say, "SELL GOLD!!!", "Sender", ["sender"]));
    }

    [Fact]
    public void NormalizationCollapsesOnlyAsciiSpacesAndUsesLocaleCase()
    {
        HudChatAntiSpamCatalog catalog = Catalog();

        Assert.Equal(200, catalog.Score(HudChatChannel.Zone, "  cheap   gold  ", "Sender", []));
        Assert.Equal(350, catalog.Score(HudChatChannel.Zone, "ДЕШЕВО", "Sender", []));
    }

    [Theory]
    [InlineData("*gold*", "gold", true)]
    [InlineData("g?ld", "gold", true)]
    [InlineData("g?ld", "gld", false)]
    [InlineData("*gold", "golden", false)]
    public void WildcardGrammarIsOnlyStarAndQuestion(string pattern, string value, bool expected) =>
        Assert.Equal(expected ? 100 : 0, PatternScore(pattern, value));

    private static int PatternScore(string pattern, string value) =>
        new HudChatAntiSpamCatalog(
            "en-US",
            [
                new HudChatAntiSpamCategory(
                    "pattern",
                    100,
                    [
                        new HudChatAntiSpamFilter.WeightedWildcards(
                            100,
                            string.Empty,
                            [new HudChatAntiSpamPattern(pattern, 100)]),
                    ]),
            ]).Score(HudChatChannel.Say, value, "Sender", []);

    private static HudChatAntiSpamCatalog Catalog() =>
        new(
            "ru-RU",
            [
                new HudChatAntiSpamCategory(
                    "trade",
                    100,
                    [
                        new HudChatAntiSpamFilter.CapsLock(250),
                        new HudChatAntiSpamFilter.Trash(250, "!"),
                        new HudChatAntiSpamFilter.WeightedWildcards(
                            100,
                            "!",
                            [
                                new HudChatAntiSpamPattern("*gold*", 100),
                                new HudChatAntiSpamPattern("*gold*", 100),
                                new HudChatAntiSpamPattern("*дешево*", 100),
                            ]),
                    ]),
            ]);
}
