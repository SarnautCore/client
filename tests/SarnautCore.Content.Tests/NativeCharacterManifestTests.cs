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
}
