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
}
