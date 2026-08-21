using SarnautCore.Networking;
using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class EntityNamingTests
{
    [Fact]
    public void PrefersTheLocalizedStringWhenThereIsOne()
    {
        Assert.Equal("Tide Crab", EntityNaming.Resolve("TideCrab.Name.txt", "Tide Crab"));
    }

    // Godot's translation server echoes the key back when it has no entry for
    // it, so an answer equal to the key is a miss and not a translation.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TideCrab.Name.txt")]
    public void FallsBackToASlugWhenTheLookupMisses(string? localized)
    {
        Assert.Equal("Tide Crab", EntityNaming.Resolve("TideCrab.Name.txt", localized));
    }

    [Theory]
    // The file-shaped keys the classic tree authors.
    [InlineData("TideCrab.Name.txt", "Tide Crab")]
    [InlineData("AirElemental1_1_Name.txt", "Air Elemental")]
    [InlineData("Rat1_1_Name.txt", "Rat")]
    [InlineData("LI_Necromancer_Name.txt", "LI Necromancer")]
    // The dotted keys the demo pack authors.
    [InlineData("mob.fixture.critter.name", "Critter")]
    [InlineData("mob.paper-harbor.tide-crab.name", "Tide Crab")]
    // Nothing sensible to shorten: the key is its own best answer.
    [InlineData("Guard", "Guard")]
    [InlineData("1234", "1234")]
    public void SlugsAKeyIntoSomethingReadable(string nameKey, string expected)
    {
        Assert.Equal(expected, EntityNaming.Slug(nameKey));
    }

    [Fact]
    public void Internal_creature_paths_never_become_nameplate_text()
    {
        const string key = "Creatures/ZombieWarrior/Instances/InstLeague1/ZombieWarriorStartInst_corridor_Name";

        string nameplate = $"{EntityNaming.Resolve(key, key)}  (2)";

        Assert.Equal("Zombie Warrior  (2)", nameplate);
    }

    [Theory]
    [InlineData("Corridor Shambler")]
    [InlineData("Призванный гнилостный зомби")]
    public void Preserves_a_resolved_locale_name(string localized)
    {
        const string key = "Creatures/ZombieWarrior/Instances/InstLeague1/ZombieWarriorStartInst_corridor_Name";

        Assert.Equal(localized, EntityNaming.Resolve(key, localized));
    }

    [Fact]
    public void HasNoNameForAnEntityTheShardDidNotName()
    {
        Assert.Equal(string.Empty, EntityNaming.Resolve(null, null));
        Assert.Equal(string.Empty, EntityNaming.Slug(string.Empty));
    }

    [Theory]
    [InlineData("Characters/Kania_male/Instances/InstLeague1/LI_Paladin", "LI Paladin")]
    [InlineData("Characters/Kania Male/Instances/Inst League1/LI Paladin", "LI Paladin")]
    public void Extracts_last_segment_from_character_paths(string pathKey, string expected)
    {
        string result = EntityNaming.Slug(pathKey);
        Assert.Equal(expected, result);
        Assert.DoesNotContain("/", result);
    }

    [Fact]
    public void Creatures_path_regression_still_uses_family_segment()
    {
        const string key = "Creatures/ZombieWarrior/Instances/InstLeague1/ZombieWarriorStartInst_Name";
        const string expected = "Zombie Warrior";

        string result = EntityNaming.Slug(key);

        Assert.Equal(expected, result);
        Assert.DoesNotContain("/", result);
    }

    [Fact]
    public void Deep_path_with_underscores_and_mixed_case()
    {
        const string key = "Some/Deep/Path/With/ManySegments/MyCreature_Boss_1_Name";
        string result = EntityNaming.Slug(key);

        Assert.DoesNotContain("/", result);
        Assert.Equal("My Creature Boss", result);
    }

    [Fact]
    public void Path_with_trailing_whitespace_and_level_suffix()
    {
        const string key = "Objects/Special/Items/SuperPotion_Name  ";
        string result = EntityNaming.Slug(key);

        Assert.DoesNotContain("/", result);
        // Should extract "SuperPotion_Name  ", trim it to "SuperPotion_Name"
        Assert.Equal("Super Potion", result);
    }

    [Fact]
    public void Plain_non_path_id_unchanged()
    {
        const string id = "SimpleNameNoPath";
        string result = EntityNaming.Slug(id);

        Assert.DoesNotContain("/", result);
        Assert.Equal("Simple Name No Path", result);
    }

    [Theory]
    [InlineData("Quest/Overlay/M2/Slay/Earth/Elementals/Title")]
    [InlineData("Item/Quest-Items/Inst-League1/Heal-Elixir")]
    [InlineData("Characters/Kania_Male/Instances/Inst_League1/LI_Paladin")]
    [InlineData("Characters/Kania_Male/Instances/Inst_League1/LI_Paladin/")]
    [InlineData("Characters\\Kania_Male\\Instances\\LI_Paladin\\")]
    public void No_path_result_ever_contains_slash(string pathId)
    {
        string result = EntityNaming.Slug(pathId);
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
    }
}
