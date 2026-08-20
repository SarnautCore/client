using System.Diagnostics;
using System.Globalization;
using Sarnaut.Protocol.V1;
using SarnautCore.Networking;

namespace SarnautCore.EntityBench;

/// <summary>
/// Measures one frame of the zone's entity update at a realistic crowd, before
/// and after the registry rewrite.
/// </summary>
/// <remarks>
/// The work being timed is the part of <c>ZoneNetworkLoop._Process</c> that does
/// not touch Godot: sampling every subscribed entity out of the timeline,
/// applying it to a visual, and retiring what left. Everything the shard sends
/// and everything the renderer draws is held identical between the two runs, so
/// the difference is the loop.
/// </remarks>
internal static class Program
{
    private const double InterpolationDelaySeconds = 0.125;
    private const double SendIntervalSeconds = 1.0 / 20.0;

    private static int Main(string[] args)
    {
        int entityCount = 288;
        int frames = 20_000;
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == "--entities")
            {
                entityCount = int.Parse(args[index + 1], CultureInfo.InvariantCulture);
            }
            else if (args[index] == "--frames")
            {
                frames = int.Parse(args[index + 1], CultureInfo.InvariantCulture);
            }
        }

        Console.WriteLine($"entities={entityCount} frames={frames} runtime={Environment.Version}");
        Result before = MeasureLegacy(entityCount, frames);
        Result after = MeasureRegistry(entityCount, frames);

        Console.WriteLine();
        Console.WriteLine("| path | update us/frame | update bytes/frame | intake us/frame | intake bytes/frame | entities |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|");
        Console.WriteLine(before.Row("before (3badf4c)", entityCount));
        Console.WriteLine(after.Row("after (registry)", entityCount));
        Console.WriteLine();
        Console.WriteLine(
            $"entity update x{before.MicrosecondsPerFrame / Math.Max(after.MicrosecondsPerFrame, 0.0001):F1} faster, "
            + $"garbage {before.BytesPerFrame:N0} -> {after.BytesPerFrame:N0} bytes per frame");
        return 0;
    }

    private static Result MeasureLegacy(int entityCount, int frames)
    {
        var timeline = new LegacySnapshotTimeline();
        var loop = new LegacyEntityUpdate(timeline);
        return Measure(
            entityCount,
            frames,
            timeline.Add,
            (clock) => loop.UpdateEntities(clock, InterpolationDelaySeconds, ownEntityId: 1),
            () => loop.Count);
    }

    private static Result MeasureRegistry(int entityCount, int frames)
    {
        var timeline = new SnapshotTimeline();
        var registry = new EntityRegistry(new CountingVisualFactory());
        return Measure(
            entityCount,
            frames,
            timeline.Add,
            (clock) => registry.Reconcile(timeline.OpenWindow(clock, InterpolationDelaySeconds), localEntityId: 1),
            () => registry.Count);
    }

    /// <summary>
    /// Runs the same frame schedule against both paths: a render frame at 60Hz
    /// and a snapshot at 20Hz, which is what the zone actually sees.
    /// </summary>
    /// <remarks>
    /// The batches are built up front and replayed, so what is timed is taking a
    /// tick and drawing a frame from it and never the cost of inventing test
    /// data. Both paths are charged for their own <c>Add</c>, because the new
    /// one builds its by-id index there and hiding that would flatter it.
    /// </remarks>
    private static Result Measure(
        int entityCount,
        int frames,
        Action<SnapshotBatch, double> feed,
        Action<double> update,
        Func<int> count)
    {
        const double FrameSeconds = 1.0 / 60.0;
        const int BatchRing = 64;
        SnapshotBatch[] batches = new SnapshotBatch[BatchRing];
        for (int index = 0; index < BatchRing; index++)
        {
            batches[index] = Batch((ulong)index + 1, entityCount, index * 0.1f);
        }

        double clock = 0;
        ulong tick = 0;
        double nextSnapshot = 0;
        double updateMilliseconds = 0;
        double intakeMilliseconds = 0;
        long updateBytes = 0;
        long intakeBytes = 0;
        var watch = new Stopwatch();

        void RunFrames(int howMany, bool measured)
        {
            for (int index = 0; index < howMany; index++)
            {
                clock += FrameSeconds;
                SnapshotBatch? batch = null;
                if (clock >= nextSnapshot)
                {
                    nextSnapshot += SendIntervalSeconds;
                    // Ticks must keep rising: the timeline drops anything older
                    // than the newest it has published.
                    batch = batches[tick % BatchRing].Clone();
                    batch.ServerTick = ++tick;
                }

                if (batch is not null)
                {
                    long before = measured ? GC.GetAllocatedBytesForCurrentThread() : 0;
                    if (measured)
                    {
                        watch.Restart();
                    }

                    feed(batch, clock);
                    if (measured)
                    {
                        watch.Stop();
                        intakeMilliseconds += watch.Elapsed.TotalMilliseconds;
                        intakeBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                    }
                }

                long bytesBefore = measured ? GC.GetAllocatedBytesForCurrentThread() : 0;
                if (measured)
                {
                    watch.Restart();
                }

                update(clock);
                if (!measured)
                {
                    continue;
                }

                watch.Stop();
                updateMilliseconds += watch.Elapsed.TotalMilliseconds;
                updateBytes += GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
            }
        }

        RunFrames(2000, measured: false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        RunFrames(frames, measured: true);

        return new Result(
            updateMilliseconds * 1000 / frames,
            (double)updateBytes / frames,
            intakeMilliseconds * 1000 / frames,
            (double)intakeBytes / frames,
            count());
    }

    private static SnapshotBatch Batch(ulong tick, int entityCount, float drift)
    {
        var batch = new SnapshotBatch { ServerTick = tick, ChunkCount = 1 };
        for (int index = 0; index < entityCount; index++)
        {
            ulong entityId = (ulong)index + 1;
            batch.Entities.Add(new EntitySnapshot
            {
                EntityId = entityId,
                Kind = entityId == 1 ? EntityKind.Player : EntityKind.Npc,
                Position = new Vec3 { X = (entityId * 0.7f) + drift, Y = entityId * 0.3f, Z = 130 },
                Velocity = new Vec3 { X = 1 },
                Heading = 0.4f,
                AnimationState = AnimationState.Moving,
                ContentId = "mob.inst-league1.rat.rat1-1",
                NameKey = "Rat1_1_Name.txt",
                Level = 2,
                Faction = "faction.wild",
                Health = 40,
                MaxHealth = 60,
                Alive = true,
            });
        }

        return batch;
    }

    private sealed record Result(
        double MicrosecondsPerFrame,
        double BytesPerFrame,
        double IntakeMicrosecondsPerFrame,
        double IntakeBytesPerFrame,
        int Entities)
    {
        internal string Row(string label, int expectedEntities)
        {
            string entities = Entities == expectedEntities - 1
                ? Entities.ToString(CultureInfo.InvariantCulture)
                : $"{Entities} (expected {expectedEntities - 1})";
            return $"| {label} | {MicrosecondsPerFrame:F1} | {BytesPerFrame:N0} | "
                + $"{IntakeMicrosecondsPerFrame:F1} | {IntakeBytesPerFrame:N0} | {entities} |";
        }
    }

    /// <summary>
    /// Stands in for the scene nodes, so the two runs differ only in the loop
    /// and not in what a visual costs.
    /// </summary>
    private sealed class CountingVisualFactory : IEntityVisualFactory
    {
        public IEntityVisual Create(SampledEntity sample) => new CountingVisual(sample.EntityId + 10_000);

        private sealed class CountingVisual(ulong pickKey) : IEntityVisual
        {
            public ulong PickKey { get; } = pickKey;

            public void Apply(SampledEntity sample)
            {
                X = sample.X;
                Y = sample.Z;
                Z = sample.Y;
                Heading = sample.Heading;
            }

            public void Retire()
            {
            }

            private float X { get; set; }

            private float Y { get; set; }

            private float Z { get; set; }

            private float Heading { get; set; }
        }
    }
}
