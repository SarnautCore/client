using Xunit;

namespace SarnautCore.NativeHud.Tests;

public sealed class HudChatCompositionTests
{
    [Fact]
    public void UnprefixedTextIsExactSayAndEnterAlwaysClearsAndCloses()
    {
        var composer = NewComposer();
        composer.Open();
        Assert.True(composer.TrySetText("  hello  "));

        HudChatCommit commit = composer.Enter(100);

        Assert.Equal(HudChatCommitKind.Submit, commit.Kind);
        Assert.Equal(HudChatChannel.Say, commit.Submission?.Channel);
        Assert.Equal("  hello  ", commit.Submission?.Text);
        Assert.False(composer.IsOpen);
        Assert.Equal(string.Empty, composer.Text);
    }

    [Fact]
    public void SlashAndBackslashPrefixesUseTypedChannelAndTargetWithoutRewritingBody()
    {
        var composer = NewComposer();
        composer.Open();
        Assert.True(composer.TrySetText("/tell Alice  hello "));

        HudChatSubmission whisper = Assert.IsType<HudChatSubmission>(composer.Enter(0).Submission);
        Assert.Equal(HudChatChannel.Whisper, whisper.Channel);
        var target = Assert.IsType<HudChatTarget.WhisperCharacterName>(whisper.Target);
        Assert.Equal("Alice", target.Value);
        Assert.Equal(" hello ", whisper.Text);

        composer.Open();
        Assert.True(composer.TrySetText("\\party hello"));
        HudChatSubmission party = Assert.IsType<HudChatSubmission>(composer.Enter(1000).Submission);
        Assert.Equal(HudChatChannel.Party, party.Channel);
        Assert.Equal("hello", party.Text);
    }

    [Fact]
    public void WhitespaceOnlyMessageIsValidAndNeverTrimmed()
    {
        var composer = NewComposer();
        composer.Open();
        Assert.True(composer.TrySetText("   "));

        HudChatCommit commit = composer.Enter(0);

        Assert.Equal(HudChatCommitKind.Submit, commit.Kind);
        Assert.Equal("   ", commit.Submission?.Text);
    }

    [Fact]
    public void TextLimitUsesUtf16CodeUnitsAndNeverSplitsSurrogatePair()
    {
        var composer = NewComposer();
        composer.Open();
        string source = new string('x', 299) + "\U0001F600" + "tail";

        Assert.True(composer.TrySetText(source));

        Assert.Equal(299, composer.Text.Length);
        Assert.Equal(new string('x', 299), composer.Text);
        Assert.False(composer.TrySetText("bad\uD800"));
        Assert.Equal(new string('x', 299), composer.Text);
    }

    [Fact]
    public void ThrottleAllowsOneAcceptedSendPerThousandMilliseconds()
    {
        var composer = NewComposer();
        composer.Open();
        composer.TrySetText("one");
        Assert.Equal(HudChatCommitKind.Submit, composer.Enter(50).Kind);

        composer.Open();
        composer.TrySetText("two");
        Assert.Equal(HudChatCommitKind.Throttled, composer.Enter(1049).Kind);

        composer.Open();
        composer.TrySetText("three");
        Assert.Equal(HudChatCommitKind.Submit, composer.Enter(1050).Kind);
    }

    [Fact]
    public void AutocompletePreservesCatalogOrderCapsResultsAndHasNoSentHistory()
    {
        var commands = Enumerable.Range(0, 30)
            .Select(index => Command($"command-{index}", $"a{index:00}"))
            .ToArray();
        var composer = new HudChatComposer(new HudChatCommandCatalog(['/', '\\'], commands, 22));
        composer.Open();
        composer.TrySetText("/a");

        Assert.Equal(22, composer.Suggestions.Length);
        Assert.Equal("a00", composer.Suggestions[0].Alias);
        Assert.Equal("a21", composer.Suggestions[21].Alias);
        Assert.True(composer.MoveSuggestion(-1));
        Assert.Equal(21, composer.SelectedSuggestionIndex);
        Assert.True(composer.MoveSuggestion(1));
        Assert.Equal(0, composer.SelectedSuggestionIndex);
        Assert.True(composer.ApplySelectedSuggestion());
        Assert.Equal("/a00 ", composer.Text);

        composer.Enter(0);
        composer.Open();
        Assert.Empty(composer.Suggestions.ToArray());
        Assert.False(composer.MoveSuggestion(1));
    }

    [Fact]
    public void TradeIsLocalAndUnsupportedCommandsNeverBecomeChat()
    {
        var composer = NewComposer();
        composer.Open();
        composer.TrySetText("/trade");
        HudChatCommit trade = composer.Enter(0);
        Assert.Equal(HudChatCommitKind.OpenTrade, trade.Kind);
        Assert.Null(trade.Submission);

        composer.Open();
        composer.TrySetText("/dance now");
        HudChatCommit unsupported = composer.Enter(0);
        Assert.Equal(HudChatCommitKind.Unsupported, unsupported.Kind);
        Assert.Null(unsupported.Submission);
    }

    [Fact]
    public void EmptyOrIncompleteCommandClosesWithoutSending()
    {
        var composer = NewComposer();
        foreach (string text in new[] { string.Empty, "/", "/say", "/tell", "/tell Alice" })
        {
            composer.Open();
            composer.TrySetText(text);
            Assert.Equal(HudChatCommitKind.None, composer.Enter(0).Kind);
            Assert.False(composer.IsOpen);
            Assert.Equal(string.Empty, composer.Text);
        }
    }

    [Fact]
    public void CatalogRejectsDuplicateAliasesIgnoringCase()
    {
        Assert.Throws<ArgumentException>(() => new HudChatCommandCatalog(
            ['/'],
            [Command("first", "same"), Command("second", "SAME")],
            22));
    }

    private static HudChatComposer NewComposer()
    {
        HudChatCommandDefinition[] commands =
        [
            Command("say", "s", "say"),
            new("tell", HudChatCommandAction.Send, HudChatChannel.Whisper,
                HudChatTargetKind.WhisperCharacterName, 1, ["t", "tell"]),
            Command("party", "p", "party", HudChatChannel.Party),
            new("trade", HudChatCommandAction.OpenTrade, default, default, -1, ["trade"]),
            new("dance", HudChatCommandAction.Unsupported, default, default, -1, ["dance"]),
        ];
        return new HudChatComposer(new HudChatCommandCatalog(['/', '\\'], commands, 22));
    }

    private static HudChatCommandDefinition Command(
        string id,
        string alias,
        string? secondAlias = null,
        HudChatChannel channel = HudChatChannel.Say) =>
        new(
            id,
            HudChatCommandAction.Send,
            channel,
            HudChatTargetKind.None,
            0,
            secondAlias is null ? [alias] : [alias, secondAlias]);
}
