using Sarnaut.Protocol.V1;
using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class AbilityBarViewModelTests
{
    [Fact]
    public void Successful_cast_runs_the_global_cooldown_to_zero()
    {
        var bar = new AbilityBarViewModel([
            new AbilityDefinition("ability.m2.strike", "ability.m2.strike.name", string.Empty),
        ]);

        bar.Apply(new CombatEvent
        {
            CasterId = 7,
            AbilityId = "ability.m2.strike",
            Rejection = AbilityRejection.None,
        }, ownEntityId: 7);

        Assert.True(bar.IsOnGlobalCooldown);
        Assert.Equal(1, bar.GlobalCooldownRemainingSeconds);
        Assert.Equal(1, bar.CooldownFraction);

        bar.Advance(0.4);

        Assert.Equal(0.6, bar.GlobalCooldownRemainingSeconds, 6);
        Assert.True(bar.IsOnGlobalCooldown);

        bar.Advance(0.7);

        Assert.Equal(0, bar.GlobalCooldownRemainingSeconds);
        Assert.False(bar.IsOnGlobalCooldown);
    }

    [Fact]
    public void Rejected_cast_does_not_start_a_cooldown()
    {
        var bar = new AbilityBarViewModel([
            new AbilityDefinition("ability.m2.strike", "ability.m2.strike.name", string.Empty),
        ]);

        bar.Apply(new CombatEvent
        {
            CasterId = 7,
            AbilityId = "ability.m2.strike",
            Rejection = AbilityRejection.OutOfRange,
        }, ownEntityId: 7);

        Assert.False(bar.IsOnGlobalCooldown);
    }
}
