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
    public void HasNoNameForAnEntityTheShardDidNotName()
    {
        Assert.Equal(string.Empty, EntityNaming.Resolve(null, null));
        Assert.Equal(string.Empty, EntityNaming.Slug(string.Empty));
    }
}
