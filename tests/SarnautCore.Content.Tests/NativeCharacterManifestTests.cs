using SarnautCore.Content;
using Xunit;

namespace SarnautCore.Content.Tests;

public sealed class NativeCharacterManifestTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "character-manifest.json");

    [Fact]
    public void Fixture_resolves_server_decimal_and_player_keys()
    {
        NativeCharacterManifest manifest = NativeCharacterManifest.Parse(File.ReadAllText(FixturePath));

        Assert.Equal(4, manifest.CharacterCount);
        Assert.Equal(2, manifest.ContentIdCount);
        Assert.Equal(3, manifest.IdentityCount);

        Assert.True(manifest.TryResolve("mob.inst-league1.rat.rat1-1", out NativeCharacterModel rat));
        Assert.Equal("Rat1_1", rat.IdentityId);
        Assert.Equal("Rat1_1/Rat1_1.tscn", rat.ScenePath);
        Assert.Equal(1.0f, rat.Scale);
        Assert.Contains("run", rat.Clips);
        Assert.Contains("attack", rat.CombatEventClips);
        Assert.NotNull(rat.Lod);
        Assert.Equal(3, rat.Lod.Levels);
        Assert.Equal([12.0f, 28.0f], rat.Lod.SwitchDistances);
        Assert.Equal(0, rat.Lod.GetLevelAtDistance(0.0f));
        Assert.Equal(0, rat.Lod.GetLevelAtDistance(11.999f));
        Assert.Equal(1, rat.Lod.GetLevelAtDistance(12.0f));
        Assert.Equal(1, rat.Lod.GetLevelAtDistance(27.999f));
        Assert.Equal(2, rat.Lod.GetLevelAtDistance(28.0f));
        Assert.Equal(2, rat.Lod.GetLevelAtDistance(500.0f));
        Assert.Equal(2, rat.Lod.Attachments.Count);
        NativeCharacterAttachmentLod attachment = rat.Lod.Attachments["Attach_Test"];
        Assert.Equal(3, attachment.Levels);
        Assert.Equal([8.0f, 20.0f], attachment.SwitchDistances);
        NativeCharacterAttachmentLod single = rat.Lod.Attachments["Attach_Single"];
        Assert.Equal(1, single.Levels);
        Assert.Empty(single.SwitchDistances);

        Assert.True(manifest.TryResolve("50032", out NativeCharacterModel decimalRat));
        Assert.Same(rat, decimalRat);

        Assert.True(manifest.TryResolvePlayer("player.kania.female", out NativeCharacterModel player));
        Assert.Equal("player.kania.female", player.IdentityId);
        Assert.True(manifest.TryResolvePlayer("chargen.league.warrior", out NativeCharacterModel chargen));
        Assert.Equal("chargen", chargen.Kind);
        Assert.False(manifest.TryResolvePlayer("mob.inst-league1.rat.rat1-1", out _));
    }

    [Fact]
    public void Missing_key_is_an_ordinary_miss()
    {
        NativeCharacterManifest manifest = NativeCharacterManifest.Parse(File.ReadAllText(FixturePath));

        Assert.False(manifest.TryResolve("mob.missing", out _));
        Assert.False(manifest.TryResolve(string.Empty, out _));
    }

    [Fact]
    public void Broken_identity_reference_is_rejected()
    {
        string json = File.ReadAllText(FixturePath)
            .Replace("\"identity_id\": \"Rat1_1\"", "\"identity_id\": \"missing\"", StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => NativeCharacterManifest.Parse(json));

        Assert.Contains("missing identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scene_cannot_escape_the_characters_directory()
    {
        string json = File.ReadAllText(FixturePath)
            .Replace("Rat1_1/Rat1_1.tscn", "../Rat1_1.tscn", StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => NativeCharacterManifest.Parse(json));

        Assert.Contains("invalid manifest-relative scene", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[12.0]", "switch distances")]
    [InlineData("[12.0, 12.0]", "positive, and increasing")]
    [InlineData("[12.0, 8.0]", "positive, and increasing")]
    [InlineData("[0.0, 28.0]", "positive, and increasing")]
    public void Invalid_lod_switches_are_rejected(string distances, string expectedMessage)
    {
        string json = File.ReadAllText(FixturePath)
            .Replace("[12.0, 28.0]", distances, StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => NativeCharacterManifest.Parse(json));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lod_distance_rejects_negative_or_nonfinite_input()
    {
        NativeCharacterManifest manifest = NativeCharacterManifest.Parse(File.ReadAllText(FixturePath));
        Assert.True(manifest.TryResolve("50032", out NativeCharacterModel rat));

        Assert.Throws<ArgumentOutOfRangeException>(() => rat.Lod!.GetLevelAtDistance(-0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => rat.Lod!.GetLevelAtDistance(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => rat.Lod!.GetLevelAtDistance(float.PositiveInfinity));
    }

    [Fact]
    public void Invalid_single_level_attachment_switch_is_rejected()
    {
        string json = File.ReadAllText(FixturePath)
            .Replace(
                "\"node\": \"Attach_Single\",\n            \"levels\": 1,\n            \"switch_distances\": []",
                "\"node\": \"Attach_Single\",\n            \"levels\": 1,\n            \"switch_distances\": [9.0]",
                StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => NativeCharacterManifest.Parse(json));

        Assert.Contains("switch distances for 1 levels", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
