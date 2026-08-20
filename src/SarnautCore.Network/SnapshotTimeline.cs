using System.Runtime.InteropServices;
using Sarnaut.Protocol.V1;

namespace SarnautCore.Networking;

/// <summary>
/// One entity at one instant. Position, heading and velocity are interpolated;
/// identity and combat state are taken from the newer of the two snapshots,
/// because a level or a content id between two values is not a thing.
/// </summary>
public readonly record struct SampledEntity(
    ulong EntityId,
    EntityKind Kind,
    float X,
    float Y,
    float Z,
    float Heading,
    float VelocityX,
    float VelocityY,
    float VelocityZ,
    AnimationState AnimationState,
    string ContentId,
    string NameKey,
    uint Level,
    string Faction,
    int Health,
    int MaxHealth,
    bool Alive)
{
    /// <summary>Builds a non-interpolated sample from a reliable spawn event.</summary>
    public static SampledEntity FromSnapshot(EntitySnapshot entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Vec3 position = entity.Position ?? EmptyVec;
        Vec3 velocity = entity.Velocity ?? EmptyVec;
        return new SampledEntity(
            entity.EntityId,
            entity.Kind,
            position.X,
            position.Y,
            position.Z,
            entity.Heading,
            velocity.X,
            velocity.Y,
            velocity.Z,
            entity.AnimationState,
            entity.ContentId,
            entity.NameKey,
            entity.Level,
            entity.Faction,
            entity.Health,
            entity.MaxHealth,
            entity.Alive);
    }

    private static readonly Vec3 EmptyVec = new();
}

/// <summary>
/// The two snapshots the render clock currently sits between, and the entity
/// set of the newest published tick.
/// </summary>
/// <remarks>
/// Sampling used to search the timeline once per entity, which made a frame
/// O(entities x snapshots) with a linear scan of the batch inside each step. A
/// window is chosen once per frame and then answers every entity from the
/// per-snapshot index, so a frame is O(entities) and allocates nothing.
/// </remarks>
public readonly ref struct SnapshotWindow
{
    private readonly SnapshotTimeline.TimedSnapshot? _before;
    private readonly SnapshotTimeline.TimedSnapshot? _after;
    private readonly float _amount;
    private readonly ReadOnlySpan<ulong> _entityIds;

    internal SnapshotWindow(
        SnapshotTimeline.TimedSnapshot? before,
        SnapshotTimeline.TimedSnapshot? after,
        float amount,
        ReadOnlySpan<ulong> entityIds)
    {
        _before = before;
        _after = after;
        _amount = amount;
        _entityIds = entityIds;
    }

    /// <summary>True when no tick has been published yet.</summary>
    public bool IsEmpty => _after is null;

    /// <summary>
    /// The entities of the newest published tick, in the order the shard sent
    /// them. Backed by the timeline's own buffer, so reading it allocates
    /// nothing and it is only valid until the next <see cref="SnapshotTimeline.Add"/>.
    /// </summary>
    public ReadOnlySpan<ulong> EntityIds => _entityIds;

    public bool TrySample(ulong entityId, out SampledEntity sample)
    {
        sample = default;
        if (_before is null || _after is null)
        {
            return false;
        }

        EntitySnapshot? left = _before.Find(entityId);
        EntitySnapshot? right = _after.Find(entityId);
        if (left is null && right is null)
        {
            return false;
        }

        left ??= right;
        right ??= left;
        Vec3 leftPosition = left!.Position ?? EmptyVec;
        Vec3 rightPosition = right!.Position ?? EmptyVec;
        Vec3 leftVelocity = left.Velocity ?? EmptyVec;
        Vec3 rightVelocity = right.Velocity ?? EmptyVec;
        sample = new SampledEntity(
            entityId,
            right.Kind,
            Lerp(leftPosition.X, rightPosition.X, _amount),
            Lerp(leftPosition.Y, rightPosition.Y, _amount),
            Lerp(leftPosition.Z, rightPosition.Z, _amount),
            LerpAngle(left.Heading, right.Heading, _amount),
            Lerp(leftVelocity.X, rightVelocity.X, _amount),
            Lerp(leftVelocity.Y, rightVelocity.Y, _amount),
            Lerp(leftVelocity.Z, rightVelocity.Z, _amount),
            right.AnimationState,
            right.ContentId,
            right.NameKey,
            right.Level,
            right.Faction,
            right.Health,
            right.MaxHealth,
            right.Alive);
        return true;
    }

    /// <summary>
    /// A missing <c>Vec3</c> reads as the origin. Shared rather than allocated
    /// per sample: nothing writes to it.
    /// </summary>
    private static readonly Vec3 EmptyVec = new();

    private static float Lerp(float left, float right, float amount) => left + ((right - left) * amount);

    private static float LerpAngle(float left, float right, float amount)
    {
        float delta = MathF.IEEERemainder(right - left, MathF.Tau);
        return left + (delta * amount);
    }
}

public sealed class SnapshotTimeline(int capacity = 32)
{
    private readonly int _capacity = capacity > 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly List<TimedSnapshot> _snapshots = [];

    /// <summary>
    /// The entity ids of the newest published tick, rebuilt when a tick is
    /// published rather than on every read: the zone loop reads it twice a frame
    /// and a fresh array each time is pure garbage.
    /// </summary>
    private readonly List<ulong> _latestIds = [];

    /// <summary>
    /// The chunks of the tick currently being reassembled, by chunk index. At
    /// most one tick is held: a tick that never completes is abandoned as soon as
    /// a newer one starts, because snapshot delivery is lossy by design and
    /// waiting for a lost datagram would stall replication behind it.
    /// </summary>
    private readonly Dictionary<uint, SnapshotBatch> _pending = [];
    private ulong _pendingTick;

    public ulong LatestServerTick => _snapshots.Count == 0 ? 0 : _snapshots[^1].Batch.ServerTick;

    public IReadOnlyList<ulong> LatestEntityIds => _latestIds;

    /// <summary>
    /// Takes one batch from the wire. The timeline takes ownership of it: every
    /// batch is a freshly parsed message and the caller must not keep or reuse
    /// the instance it handed over.
    /// </summary>
    /// <remarks>
    /// A snapshot too large for one datagram arrives as <c>ChunkCount</c>
    /// batches sharing a <c>ServerTick</c> (session spec rule 5.5.7). They are
    /// held here until the tick is whole and only then published, because a
    /// chunk is a fragment of the world and not a view of it: a consumer that
    /// despawns everything outside <see cref="LatestEntityIds"/> would destroy
    /// and recreate every entity that landed in a sibling chunk, every frame.
    /// </remarks>
    public void Add(SnapshotBatch batch, double receivedAtSeconds)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!double.IsFinite(receivedAtSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(receivedAtSeconds));
        }

        if (_snapshots.Count > 0 && batch.ServerTick < _snapshots[^1].Batch.ServerTick)
        {
            return;
        }

        SnapshotBatch? whole = Reassemble(batch);
        if (whole is null)
        {
            return;
        }

        if (_snapshots.Count > 0 && whole.ServerTick == _snapshots[^1].Batch.ServerTick)
        {
            _snapshots[^1] = new TimedSnapshot(whole, receivedAtSeconds);
        }
        else
        {
            _snapshots.Add(new TimedSnapshot(whole, receivedAtSeconds));
        }

        if (_snapshots.Count > _capacity)
        {
            _snapshots.RemoveRange(0, _snapshots.Count - _capacity);
        }

        PublishLatestIds();
    }

    /// <summary>
    /// Returns the whole tick once every chunk of it has arrived, or null while
    /// it is still incomplete. A count of 0 or 1 is a batch that was never
    /// chunked, which is every batch on the reliable fallback.
    /// </summary>
    private SnapshotBatch? Reassemble(SnapshotBatch batch)
    {
        if (batch.ChunkCount <= 1)
        {
            _pending.Clear();
            return batch;
        }

        if (_pending.Count > 0 && batch.ServerTick < _pendingTick)
        {
            // A chunk of a tick a newer one has already overtaken. Datagrams
            // reorder; resurrecting it would publish the world backwards.
            return null;
        }

        if (_pending.Count == 0 || batch.ServerTick != _pendingTick)
        {
            _pending.Clear();
            _pendingTick = batch.ServerTick;
        }

        if (batch.ChunkIndex >= batch.ChunkCount)
        {
            // A chunk claiming to be past the end of its own tick. Dropping it
            // leaves the tick incomplete, and the next one replaces it.
            return null;
        }

        _pending[batch.ChunkIndex] = batch;
        if (_pending.Count != batch.ChunkCount)
        {
            return null;
        }

        SnapshotBatch whole = new() { ServerTick = _pendingTick, ChunkCount = 1 };
        for (uint index = 0; index < batch.ChunkCount; index++)
        {
            whole.Entities.AddRange(_pending[index].Entities);
        }

        _pending.Clear();
        return whole;
    }

    private void PublishLatestIds()
    {
        _latestIds.Clear();
        if (_snapshots.Count == 0)
        {
            return;
        }

        foreach (EntitySnapshot entity in _snapshots[^1].Batch.Entities)
        {
            _latestIds.Add(entity.EntityId);
        }
    }

    /// <summary>
    /// Picks the pair of snapshots the render clock sits between, once for the
    /// whole frame.
    /// </summary>
    public SnapshotWindow OpenWindow(double nowSeconds, double interpolationDelaySeconds)
    {
        if (_snapshots.Count == 0)
        {
            return default;
        }

        double target = nowSeconds - interpolationDelaySeconds;
        TimedSnapshot before = _snapshots[0];
        TimedSnapshot after = _snapshots[^1];
        for (int index = 0; index < _snapshots.Count; index++)
        {
            TimedSnapshot current = _snapshots[index];
            if (current.ReceivedAtSeconds <= target)
            {
                before = current;
            }

            if (current.ReceivedAtSeconds >= target)
            {
                after = current;
                break;
            }
        }

        float amount = before.ReceivedAtSeconds >= after.ReceivedAtSeconds
            ? 0
            : (float)Math.Clamp(
                (target - before.ReceivedAtSeconds) / (after.ReceivedAtSeconds - before.ReceivedAtSeconds),
                0,
                1);
        return new SnapshotWindow(before, after, amount, CollectionsMarshal.AsSpan(_latestIds));
    }

    public bool TrySample(ulong entityId, double nowSeconds, double interpolationDelaySeconds, out SampledEntity sample)
    {
        return OpenWindow(nowSeconds, interpolationDelaySeconds).TrySample(entityId, out sample);
    }

    /// <summary>
    /// One received tick, with the by-id index the window samples through. The
    /// index is built once when the tick is published rather than scanned per
    /// entity per frame.
    /// </summary>
    internal sealed class TimedSnapshot
    {
        private readonly Dictionary<ulong, EntitySnapshot> _index;

        internal TimedSnapshot(SnapshotBatch batch, double receivedAtSeconds)
        {
            Batch = batch;
            ReceivedAtSeconds = receivedAtSeconds;
            _index = new Dictionary<ulong, EntitySnapshot>(batch.Entities.Count);
            foreach (EntitySnapshot entity in batch.Entities)
            {
                // A tick that repeats an id is malformed; the last one wins
                // rather than throwing on the network thread's data.
                _index[entity.EntityId] = entity;
            }
        }

        internal SnapshotBatch Batch { get; }

        internal double ReceivedAtSeconds { get; }

        internal EntitySnapshot? Find(ulong entityId) =>
            _index.TryGetValue(entityId, out EntitySnapshot? entity) ? entity : null;
    }
}
