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

    // A tick the shard could not fit in one datagram arrives as several batches
    // sharing a ServerTick (session spec rule 5.5.7). Replacing the tick with
    // each chunk as it lands — which is what a same-tick batch used to do — makes
    // LatestEntityIds the last chunk only, so the zone loop despawns and
    // recreates every entity in every other chunk, every frame.
    [Fact]
    public void MergesEveryChunkOfOneTickBeforePublishingIt()
    {
        var timeline = new SnapshotTimeline();
        timeline.Add(Chunk(tick: 10, index: 0, count: 3, entityId: 1), receivedAtSeconds: 1.0);

        // Nothing is published until the tick is whole: a chunk on its own is a
        // fragment of the world, not a view of it.
        Assert.Empty(timeline.LatestEntityIds);
        Assert.Equal((ulong)0, timeline.LatestServerTick);

        timeline.Add(Chunk(tick: 10, index: 2, count: 3, entityId: 3), receivedAtSeconds: 1.01);
        Assert.Empty(timeline.LatestEntityIds);

        timeline.Add(Chunk(tick: 10, index: 1, count: 3, entityId: 2), receivedAtSeconds: 1.02);

        Assert.Equal((ulong)10, timeline.LatestServerTick);
        // Merged in chunk order, whatever order the datagrams arrived in.
        Assert.Equal(new ulong[] { 1, 2, 3 }, timeline.LatestEntityIds);
    }

    [Fact]
    public void AbandonsAnIncompleteTickWhenANewerOneArrives()
    {
        var timeline = new SnapshotTimeline();
        timeline.Add(Chunk(tick: 10, index: 0, count: 2, entityId: 1), receivedAtSeconds: 1.0);
        timeline.Add(Snapshot(tick: 11, x: 5), receivedAtSeconds: 1.1);

        Assert.Equal((ulong)11, timeline.LatestServerTick);
        Assert.Equal(new ulong[] { 7 }, timeline.LatestEntityIds);

        // The stale chunk of tick 10 must not be able to complete tick 11.
        timeline.Add(Chunk(tick: 10, index: 1, count: 2, entityId: 2), receivedAtSeconds: 1.2);
        Assert.Equal((ulong)11, timeline.LatestServerTick);
        Assert.Equal(new ulong[] { 7 }, timeline.LatestEntityIds);
    }

    // The reliable fallback sends whole batches, and a retransmitted tick still
    // replaces rather than accumulating.
    [Fact]
    public void ReplacesRatherThanMergesAWholeBatchOfTheSameTick()
    {
        var timeline = new SnapshotTimeline();
        timeline.Add(Snapshot(tick: 10, x: 2), receivedAtSeconds: 1.0);
        timeline.Add(Snapshot(tick: 10, x: 9), receivedAtSeconds: 1.05);

        Assert.Equal(new ulong[] { 7 }, timeline.LatestEntityIds);
        Assert.True(timeline.TrySample(7, 2, 0.15, out SampledEntity sample));
        Assert.Equal(9, sample.X);
    }

    private static SnapshotBatch Chunk(ulong tick, uint index, uint count, ulong entityId)
    {
        var batch = new SnapshotBatch { ServerTick = tick, ChunkIndex = index, ChunkCount = count };
        batch.Entities.Add(new EntitySnapshot
        {
            EntityId = entityId,
            Kind = EntityKind.Npc,
            Position = new Vec3(),
            Velocity = new Vec3(),
            Alive = true,
        });
        return batch;
    }

    private static SnapshotBatch Snapshot(ulong tick, float x, int health = 150)
    {
        var batch = new SnapshotBatch { ServerTick = tick, ChunkCount = 1 };
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
