using System.Text;
using System.Text.Json;
using Xunit;

namespace SarnautCore.NativeHud.Tests;

public sealed class HudChatCommandJsonTests
{
    [Fact]
    public void ParsesSourceFreeChannelSendTradeAndUnsupportedActions()
    {
        HudChatCommandCatalog catalog = HudChatCommandJson.Parse(Encoding.UTF8.GetBytes(ValidJson));

        Assert.Equal(22, catalog.AutocompleteCapacity);
        Assert.Equal(4, catalog.CommandCount);
        Assert.Equal(10, catalog.ChannelCount);
        Assert.Equal(10, catalog.Channels.Length);
        Assert.True(catalog.TryGetPresentation(HudChatChannel.GuildOfficer, out HudChatChannelPresentation? officer));
        Assert.Equal((byte)10, officer!.ClientChatType);
        Assert.Equal("Officer", officer.LocalizedPrefix);
        Assert.Equal("LogColorGreen", officer.DefaultColorClass);
        Assert.False(officer.BubbleEligible);

        var composer = new HudChatComposer(catalog);
        composer.Open();
        Assert.True(composer.TrySetText("/whisper Alice  hello "));
        HudChatSubmission whisper = Assert.IsType<HudChatSubmission>(composer.Enter(0).Submission);
        Assert.Equal(HudChatChannel.Whisper, whisper.Channel);
        Assert.Equal(" hello ", whisper.Text);
        Assert.Equal("Alice", Assert.IsType<HudChatTarget.WhisperCharacterName>(whisper.Target).Value);

        composer.Open();
        composer.TrySetText("/trade Bob Smith");
        HudChatCommit trade = composer.Enter(0);
        Assert.Equal(HudChatCommitKind.OpenTrade, trade.Kind);
        Assert.Equal("Bob Smith", Assert.IsType<HudChatLocalAction.InviteTradeByName>(trade.LocalAction).PlayerName);

        composer.Open();
        composer.TrySetText("/dance");
        Assert.Equal(HudChatCommitKind.Unsupported, composer.Enter(0).Kind);
    }

    [Theory]
    [InlineData("\"schema\":\"sarnaut.chat-commands/v1\"", "\"schema\":\"sarnaut.chat-commands/v2\"")]
    [InlineData("\"bubble_opacity\":\"chat-bubbles-opacity\"", "\"bubble_opacity\":\"other\"")]
    [InlineData("\"client_chat_type\":2", "\"client_chat_type\":3")]
    [InlineData("\"empty_fallback\":\"selected-visible-player\"", "\"empty_fallback\":\"none\"")]
    [InlineData("\"argument_policy\":\"first-token\"", "\"argument_policy\":\"none\"")]
    public void RejectsSemanticDrift(string original, string replacement)
    {
        string invalid = ValidJson.Replace(original, replacement, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => HudChatCommandJson.Parse(Encoding.UTF8.GetBytes(invalid)));
    }

    [Fact]
    public void RejectsUnknownAndDuplicateProperties()
    {
        string unknown = ValidJson.Replace(
            "\"locale\":\"eng\",",
            "\"locale\":\"eng\",\"source_path\":\"private.xdb\",",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => HudChatCommandJson.Parse(Encoding.UTF8.GetBytes(unknown)));

        string duplicate = ValidJson.Replace(
            "\"autocomplete_capacity\":22,",
            "\"autocomplete_capacity\":22,\"autocomplete_capacity\":22,",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => HudChatCommandJson.Parse(Encoding.UTF8.GetBytes(duplicate)));
    }

    private const string ValidJson = """
        {
          "schema":"sarnaut.chat-commands/v1",
          "locale":"eng",
          "command_prefixes":["/","\\"],
          "autocomplete_capacity":22,
          "channels":[
            {"channel":"say","client_chat_type":2,"localized_prefix":"Say","default_color_class":"LogColorWhite","bubble_eligible":true},
            {"channel":"tell","client_chat_type":0,"localized_prefix":"Whisper","default_color_class":"LogColorMagenta","bubble_eligible":false},
            {"channel":"psionic","client_chat_type":12,"localized_prefix":"Telepathy","default_color_class":"LogColorGold","bubble_eligible":false},
            {"channel":"party","client_chat_type":1,"localized_prefix":"Party","default_color_class":"LogColorBlue","bubble_eligible":false},
            {"channel":"raid","client_chat_type":11,"localized_prefix":"Raid","default_color_class":"LogColorOrange","bubble_eligible":false},
            {"channel":"guild","client_chat_type":9,"localized_prefix":"Guild","default_color_class":"LogColorLightGreen","bubble_eligible":false},
            {"channel":"officer","client_chat_type":10,"localized_prefix":"Officer","default_color_class":"LogColorGreen","bubble_eligible":false},
            {"channel":"yellzone","client_chat_type":5,"localized_prefix":"Shout","default_color_class":"LogColorCian","bubble_eligible":true},
            {"channel":"zone","client_chat_type":4,"localized_prefix":"Zone","default_color_class":"LogColorBrown","bubble_eligible":true},
            {"channel":"world","client_chat_type":6,"localized_prefix":"World","default_color_class":"LogColorGold","bubble_eligible":true}
          ],
          "runtime_options":{"bubbles_enabled":"chat-bubbles-show","bubble_opacity":"chat-bubbles-opacity"},
          "commands":[
            {"id":"say","aliases":["s","say"],"argument_policy":"none","action":{"kind":"send-chat","channel":"say"}},
            {"id":"tell","aliases":["w","whisper"],"argument_policy":"first-token","action":{"kind":"send-chat","channel":"tell"}},
            {"id":"trade","aliases":["trade"],"argument_policy":"rest","action":{"kind":"trade","argument":"optional-player-name-rest","empty_fallback":"selected-visible-player","reject_npc":true}},
            {"id":"dance","aliases":["dance"],"argument_policy":"rest","action":{"kind":"emote","emote_id":"dance","animation":"EmoteDance","localized_name":"Dance","localized_description":""}}
          ]
        }
        """;
}
