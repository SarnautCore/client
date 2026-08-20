using Sarnaut.Protocol.V1;
using SarnautCore.Networking;
using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class SnapshotTimelineTests
{
    [Fact]
    public void SamplesBetweenSnapshotsAtTheConfiguredDelay()
    {
        var timeline = new SnapshotTimeline();
        timeline.Add(Snapshot(tick: 10, x: 2), receivedAtSeconds: 1.0);
        timeline.Add(Snapshot(tick: 12, x: 4), receivedAtSeconds: 1.2);

        bool found = timeline.TrySample(
            entityId: 7,
            nowSeconds: 1.25,
            interpolationDelaySeconds: 0.15,
            out SampledEntity sample);

        Assert.True(found);
        Assert.Equal(3, sample.X, precision: 4);
        Assert.Equal((ulong)12, timeline.LatestServerTick);
    }

    [Fact]
    public void IgnoresSnapshotsOlderThanTheLatestTick()
    {
        var timeline = new SnapshotTimeline();
        timeline.Add(Snapshot(tick: 12, x: 4), receivedAtSeconds: 1.2);
        timeline.Add(Snapshot(tick: 10, x: 100), receivedAtSeconds: 1.3);

        Assert.True(timeline.TrySample(7, 2, 0.15, out SampledEntity sample));
        Assert.Equal(4, sample.X);
        Assert.Equal((ulong)12, timeline.LatestServerTick);
    }

    [Fact]
    public void CarriesContentAndCombatFieldsThroughInterpolation()
    {
        var timeline = new SnapshotTimeline();
        timeline.Add(Snapshot(tick: 10, x: 2, health: 120), receivedAtSeconds: 1.0);
        timeline.Add(Snapshot(tick: 12, x: 4, health: 60), receivedAtSeconds: 1.2);

        Assert.True(timeline.TrySample(7, 1.25, 0.15, out SampledEntity sample));

        // Position interpolates; identity and combat state do not, because a
        // level or a content id halfway between two values is not a thing.
        Assert.Equal(3, sample.X, precision: 4);
        Assert.Equal("mob.fixture.critter", sample.ContentId);
        Assert.Equal("mob.fixture.critter.name", sample.NameKey);
        Assert.Equal((uint)2, sample.Level);
        Assert.Equal("faction.wild", sample.Faction);
        Assert.Equal(60, sample.Health);
        Assert.Equal(150, sample.MaxHealth);
        Assert.True(sample.Alive);
    }

    private static SnapshotBatch Snapshot(ulong tick, float x, int health = 150)
    {
        var batch = new SnapshotBatch { ServerTick = tick };
        batch.Entities.Add(new EntitySnapshot
        {
            EntityId = 7,
            Kind = EntityKind.Player,
            Position = new Vec3 { X = x },
            Velocity = new Vec3(),
            AnimationState = AnimationState.Moving,
            ContentId = "mob.fixture.critter",
            NameKey = "mob.fixture.critter.name",
            Level = 2,
            Faction = "faction.wild",
            Health = health,
            MaxHealth = 150,
            Alive = true,
        });
        return batch;
    }
}
