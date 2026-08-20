using Sarnaut.Protocol.V1;
using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class TargetViewModelTests
{
    [Fact]
    public void Target_can_be_selected_cleared_and_killed()
    {
        var target = new TargetViewModel();
        int deaths = 0;
        target.TargetDied += _ => deaths++;

        target.Select(new EntityHudSnapshot(42, "EarthElementalName", "mob.earth-elemental", 2, 120, 120, true));

        Assert.Equal((ulong)42, target.EntityId);
        Assert.Equal("EarthElementalName", target.NameKey);
        Assert.Equal(1, target.HealthFraction);

        target.Apply(new CombatEvent
        {
            TargetId = 42,
            Damage = 20,
            TargetHealth = 0,
            TargetMaxHealth = 120,
            KillingBlow = true,
            Rejection = AbilityRejection.None,
        });

        Assert.False(target.Alive);
        Assert.Equal(1, deaths);

        target.Clear();

        Assert.False(target.HasTarget);
        Assert.Equal((ulong)0, target.EntityId);
    }
}
