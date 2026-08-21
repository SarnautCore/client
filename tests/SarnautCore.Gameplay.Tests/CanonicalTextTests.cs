using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class CanonicalTextTests
{
    [Theory]
    [InlineData("quest.overlay.m2.slay-earth-elementals.title", "Slay Earth Elementals")]
    [InlineData("item.quest-items.inst-league1.heal-elixir-item-resource", "Heal Elixir")]
    [InlineData("EarthElementalName", "Earth Elemental Name")]
    public void Canonical_ids_have_readable_fallbacks(string canonicalId, string expected)
    {
        Assert.Equal(expected, CanonicalText.Fallback(canonicalId));
    }

    [Fact]
    public void Creature_resource_keys_do_not_leak_into_the_target_frame()
    {
        const string key = "Creatures/ZombieWarrior/Instances/InstLeague1/ZombieWarriorStartInst_corridor_Name";

        Assert.Equal("Zombie Warrior", CanonicalText.Fallback(key));
    }

    [Theory]
    [InlineData("Characters/Kania_male/Instances/InstLeague1/LI_Paladin")]
    [InlineData("Characters/Kania Male/Instances/Inst League1/LI Paladin")]
    public void Character_paths_extract_last_segment_without_leaking_slash(string pathId)
    {
        string result = CanonicalText.Fallback(pathId);
        Assert.DoesNotContain("/", result);
        Assert.Equal("LI Paladin", result);
    }

    [Fact]
    public void Deep_path_with_multiple_segments()
    {
        const string key = "Some/Deep/Path/With/ManySegments/MyCreature_Boss_1";
        string result = CanonicalText.Fallback(key);

        Assert.DoesNotContain("/", result);
        Assert.Equal("My Creature Boss", result);
    }

    [Theory]
    [InlineData("Quest/Overlay/M2/Slay/Earth/Elementals/Title")]
    [InlineData("Item/Quest-Items/Inst-League1/Heal-Elixir")]
    public void No_path_result_ever_contains_slash(string pathId)
    {
        string result = CanonicalText.Fallback(pathId);
        Assert.DoesNotContain("/", result);
    }
}
