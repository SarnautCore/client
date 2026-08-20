using Sarnaut.Protocol.V1;
using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class DeathFeedbackViewModelTests
{
    [Fact]
    public void Death_then_living_snapshot_reports_respawn_and_expires()
    {
        var feedback = new DeathFeedbackViewModel(lifetimeSeconds: 2);

        feedback.Apply(new DeathEvent { VictimEntityId = 42, KillerEntityId = 7 }, ownEntityId: 7);
        Assert.Equal(DeathFeedbackKind.TargetDefeated, feedback.Kind);
        Assert.True(feedback.Visible);

        feedback.Observe(new EntityHudSnapshot(42, "mob.name", "mob.earth", 2, 120, 120, true));
        Assert.Equal(DeathFeedbackKind.Respawned, feedback.Kind);

        feedback.Advance(2.1);
        Assert.False(feedback.Visible);
    }
}
