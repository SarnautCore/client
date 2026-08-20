using Sarnaut.Protocol.V1;
using SarnautCore.Networking;
using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class EntityRegistryTests
{
    private const double Delay = 0.1;

    [Fact]
    public void ReliableSpawnAddsAVisual()
    {
        var factory = new FakeEntityVisualFactory();
        var registry = new EntityRegistry(factory);

        Spawn(registry, SnapshotFixtures.Batch(tick: 1, entityCount: 3));

        Assert.Equal(3, registry.Count);
        Assert.Equal(3, factory.CreateCount);
        Assert.True(registry.TryGet(2, out TrackedEntity? entity));
        Assert.Equal((ulong)2, entity.EntityId);
        Assert.Equal("mob.inst-league1.rat.rat1-1", entity.Latest.ContentId);
    }

    [Fact]
    public void UpdatesTheSameVisualRatherThanReplacingIt()
    {
        var factory = new FakeEntityVisualFactory();
        var registry = new EntityRegistry(factory);
        var timeline = new SnapshotTimeline();
        timeline.Add(SnapshotFixtures.Batch(tick: 1, entityCount: 2), receivedAtSeconds: 1.0);
        Spawn(registry, SnapshotFixtures.Batch(tick: 1, entityCount: 2));
        registry.Reconcile(timeline.OpenWindow(1.1, Delay), localEntityId: 0);

        timeline.Add(SnapshotFixtures.Batch(tick: 2, entityCount: 2, drift: 4), receivedAtSeconds: 1.05);
        registry.Reconcile(timeline.OpenWindow(1.15, Delay), localEntityId: 0);

        Assert.Equal(2, factory.CreateCount);
        Assert.True(registry.TryGet(1, out TrackedEntity? entity));
        Assert.Equal(3, factory[1].ApplyCount);
        Assert.Same(factory[1], entity.Visual);
        Assert.Equal(4.5f, entity.Latest.X, precision: 3);
    }

    [Fact]
    public void MissingSnapshotDoesNotDespawnButReliableEventDoes()
    {
        var factory = new FakeEntityVisualFactory();
        var registry = new EntityRegistry(factory);
        var timeline = new SnapshotTimeline();
        timeline.Add(SnapshotFixtures.Batch(tick: 1, entityCount: 3), receivedAtSeconds: 1.0);
        Spawn(registry, SnapshotFixtures.Batch(tick: 1, entityCount: 3));
        registry.Reconcile(timeline.OpenWindow(1.1, Delay), localEntityId: 0);

        timeline.Add(SnapshotFixtures.Batch(tick: 2, entityCount: 1), receivedAtSeconds: 1.05);
        registry.Reconcile(timeline.OpenWindow(1.15, Delay), localEntityId: 0);

        Assert.Equal(3, registry.Count);
        Assert.True(registry.TryGet(3, out _));
        Assert.False(factory[3].Retired);

        Assert.True(registry.Remove(3));

        Assert.Equal(2, registry.Count);
        Assert.True(factory[3].Retired);
        Assert.False(factory[1].Retired);
        Assert.False(registry.TryGetByPickKey(FakeEntityVisualFactory.PickKeyOf(3), out _));
    }

    [Fact]
    public void LateSnapshotCannotRecreateADespawnedEntity()
    {
        var factory = new FakeEntityVisualFactory();
        var registry = new EntityRegistry(factory);
        registry.Spawn(SnapshotFixtures.Entity(7), localEntityId: 0);
        Assert.True(registry.Remove(7));

        var timeline = new SnapshotTimeline();
        timeline.Add(SnapshotFixtures.Batch(tick: 2, entityCount: 7), receivedAtSeconds: 1.0);
        registry.Reconcile(timeline.OpenWindow(1.1, Delay), localEntityId: 0);

        Assert.Equal(0, registry.Count);
        Assert.Equal(1, factory.CreateCount);
        Assert.True(factory[7].Retired);
    }

    [Fact]
    public void RemoveRetiresTheVisualAndBothLookups()
    {
        var factory = new FakeEntityVisualFactory();
        var registry = new EntityRegistry(factory);
        registry.Upsert(Sample(7));

        Assert.True(registry.Remove(7));
        Assert.False(registry.Remove(7));
        Assert.Equal(0, registry.Count);
        Assert.True(factory[7].Retired);
        Assert.False(registry.TryGet(7, out _));
        Assert.False(registry.TryGetByPickKey(FakeEntityVisualFactory.PickKeyOf(7), out _));
    }

    [Fact]
    public void AnswersWhichEntityOwnsTheBodyAPickRayHit()
    {
        var factory = new FakeEntityVisualFactory();
        var registry = new EntityRegistry(factory);
        registry.Upsert(Sample(11));
        registry.Upsert(Sample(12));

        Assert.True(registry.TryGetByPickKey(FakeEntityVisualFactory.PickKeyOf(12), out TrackedEntity? hit));
        Assert.Equal((ulong)12, hit.EntityId);
        Assert.False(registry.TryGetByPickKey(999, out _));
    }

    // The player's own entity is drawn by the controller it steers, so the
    // registry must not build a second visual standing inside the first.
    [Fact]
    public void PublishesTheLocalEntityWithoutGivingItAVisual()
    {
        var factory = new FakeEntityVisualFactory();
        var registry = new EntityRegistry(factory);
        var timeline = new SnapshotTimeline();
        timeline.Add(SnapshotFixtures.Batch(tick: 1, entityCount: 3), receivedAtSeconds: 1.0);
        Spawn(registry, SnapshotFixtures.Batch(tick: 1, entityCount: 3), localEntityId: 2);

        registry.Reconcile(timeline.OpenWindow(1.1, Delay), localEntityId: 2);

        Assert.Equal(2, registry.Count);
        Assert.Equal(2, factory.CreateCount);
        Assert.False(registry.TryGet(2, out _));
        Assert.True(registry.HasLocalSample);
        Assert.Equal((ulong)2, registry.LocalSample.EntityId);
    }

    [Fact]
    public void CyclesTargetsOutwardsFromThePlayerAndWrapsRound()
    {
        var factory = new FakeEntityVisualFactory();
        var registry = new EntityRegistry(factory);
        registry.Upsert(Sample(1, x: 3));
        registry.Upsert(Sample(2, x: 1));
        registry.Upsert(Sample(3, x: 6));

        Assert.True(registry.TryCycleTarget(0, 0, 0, 0, 40, out ulong first));
        Assert.Equal((ulong)2, first);
        Assert.True(registry.TryCycleTarget(first, 0, 0, 0, 40, out ulong second));
        Assert.Equal((ulong)1, second);
        Assert.True(registry.TryCycleTarget(second, 0, 0, 0, 40, out ulong third));
        Assert.Equal((ulong)3, third);
        Assert.True(registry.TryCycleTarget(third, 0, 0, 0, 40, out ulong wrapped));
        Assert.Equal((ulong)2, wrapped);
    }

    [Fact]
    public void SkipsCorpsesAndAnythingBeyondRangeWhenCycling()
    {
        var factory = new FakeEntityVisualFactory();
        var registry = new EntityRegistry(factory);
        registry.Upsert(Sample(1, x: 2, alive: false));
        registry.Upsert(Sample(2, x: 90));
        registry.Upsert(Sample(3, x: 5));

        Assert.True(registry.TryCycleTarget(0, 0, 0, 0, 40, out ulong picked));
        Assert.Equal((ulong)3, picked);
    }

    [Fact]
    public void ReportsNoTargetWhenNothingIsInRange()
    {
        var registry = new EntityRegistry(new FakeEntityVisualFactory());
        registry.Upsert(Sample(1, x: 400));

        Assert.False(registry.TryCycleTarget(0, 0, 0, 0, 40, out ulong picked));
        Assert.Equal((ulong)0, picked);
    }

    // The zone loop runs this every frame for every subscribed entity. It used to
    // allocate an entity id array twice a frame plus a stale-id array, so the
    // garbage scaled with the crowd; a steady crowd must now cost nothing.
    [Fact]
    public void SteadyStateReconcileAllocatesNothing()
    {
        var registry = new EntityRegistry(new FakeEntityVisualFactory());
        var timeline = new SnapshotTimeline();
        ulong tick = 1;
        double clock = 1.0;

        void Pump(int updates)
        {
            for (int index = 0; index < updates; index++)
            {
                clock += 0.05;
                timeline.Add(SnapshotFixtures.Batch(++tick, SnapshotFixtures.ZoneEntityCount, drift: index), clock);
                registry.Reconcile(timeline.OpenWindow(clock + 0.02, Delay), localEntityId: 1);
            }
        }

        // Warm up: spawn every visual, JIT every path, and let the timeline's id
        // buffer reach its final capacity.
        Spawn(registry, SnapshotFixtures.Batch(tick, SnapshotFixtures.ZoneEntityCount), localEntityId: 1);
        Pump(20);
        Assert.Equal(SnapshotFixtures.ZoneEntityCount - 1, registry.Count);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ReconcileOnly(registry, timeline, clock, updates: 100);
        long firstBatch = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        ReconcileOnly(registry, timeline, clock, updates: 200);
        long doubleBatch = GC.GetAllocatedBytesForCurrentThread() - before;

        // Twice the updates must not mean twice the garbage, because the right
        // answer is no garbage at all.
        Assert.True(firstBatch == 0, $"100 updates allocated {firstBatch} bytes");
        Assert.True(doubleBatch == 0, $"200 updates allocated {doubleBatch} bytes");
    }

    private static void ReconcileOnly(EntityRegistry registry, SnapshotTimeline timeline, double clock, int updates)
    {
        for (int index = 0; index < updates; index++)
        {
            registry.Reconcile(timeline.OpenWindow(clock + 0.02, Delay), localEntityId: 1);
        }
    }

    private static void Spawn(EntityRegistry registry, SnapshotBatch batch, ulong localEntityId = 0)
    {
        foreach (EntitySnapshot entity in batch.Entities)
        {
            registry.Spawn(entity, localEntityId);
        }
    }

    private static SampledEntity Sample(ulong entityId, float x = 0, bool alive = true)
    {
        return new SampledEntity(
            entityId,
            EntityKind.Npc,
            x,
            0,
            0,
            0,
            0,
            0,
            0,
            AnimationState.Idle,
            "mob.inst-league1.rat.rat1-1",
            "Rat1_1_Name.txt",
            2,
            "faction.wild",
            40,
            60,
            alive);
    }
}
