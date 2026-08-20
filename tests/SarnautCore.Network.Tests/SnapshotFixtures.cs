using Sarnaut.Protocol.V1;

namespace SarnautCore.Network.Tests;

/// <summary>Snapshot batches shaped like the ones a shard sends.</summary>
internal static class SnapshotFixtures
{
    /// <summary>
    /// The entity count the zone measurement uses: the InstLeague1 subscription
    /// the shard hands a client standing in the middle of the instance.
    /// </summary>
    internal const int ZoneEntityCount = 288;

    internal static SnapshotBatch Batch(ulong tick, int entityCount, float drift = 0)
    {
        var batch = new SnapshotBatch { ServerTick = tick, ChunkCount = 1 };
        for (int index = 0; index < entityCount; index++)
        {
            batch.Entities.Add(Entity((ulong)index + 1, drift));
        }

        return batch;
    }

    internal static EntitySnapshot Entity(ulong entityId, float drift = 0, bool alive = true)
    {
        return new EntitySnapshot
        {
            EntityId = entityId,
            Kind = EntityKind.Npc,
            Position = new Vec3 { X = (entityId * 0.5f) + drift, Y = entityId * 0.25f, Z = 12 },
            Velocity = new Vec3 { X = 1 },
            Heading = 0.5f,
            AnimationState = AnimationState.Moving,
            ContentId = "mob.inst-league1.rat.rat1-1",
            NameKey = "Rat1_1_Name.txt",
            Level = 2,
            Faction = "faction.wild",
            Health = 40,
            MaxHealth = 60,
            Alive = alive,
        };
    }
}
