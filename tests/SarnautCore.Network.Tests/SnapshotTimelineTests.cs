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

    private static SnapshotBatch Snapshot(ulong tick, float x)
    {
        var batch = new SnapshotBatch { ServerTick = tick };
        batch.Entities.Add(new EntitySnapshot
        {
            EntityId = 7,
            Kind = EntityKind.Player,
            Position = new Vec3 { X = x },
            Velocity = new Vec3(),
            AnimationState = AnimationState.Moving,
        });
        return batch;
    }
}
