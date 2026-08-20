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
    bool Alive);

public sealed class SnapshotTimeline(int capacity = 32)
{
    private readonly int _capacity = capacity > 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly List<TimedSnapshot> _snapshots = [];

    /// <summary>
    /// The chunks of the tick currently being reassembled, by chunk index. At
    /// most one tick is held: a tick that never completes is abandoned as soon as
    /// a newer one starts, because snapshot delivery is lossy by design and
    /// waiting for a lost datagram would stall replication behind it.
    /// </summary>
    private readonly Dictionary<uint, SnapshotBatch> _pending = [];
    private ulong _pendingTick;

    public ulong LatestServerTick => _snapshots.Count == 0 ? 0 : _snapshots[^1].Batch.ServerTick;

    public IReadOnlyCollection<ulong> LatestEntityIds => _snapshots.Count == 0
        ? Array.Empty<ulong>()
        : _snapshots[^1].Batch.Entities.Select(entity => entity.EntityId).ToArray();

    /// <summary>
    /// Takes one batch from the wire.
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
            return batch.Clone();
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

        _pending[batch.ChunkIndex] = batch.Clone();
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

    public bool TrySample(ulong entityId, double nowSeconds, double interpolationDelaySeconds, out SampledEntity sample)
    {
        sample = default;
        if (_snapshots.Count == 0)
        {
            return false;
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

        EntitySnapshot? left = Find(before.Batch, entityId);
        EntitySnapshot? right = Find(after.Batch, entityId);
        if (left is null && right is null)
        {
            return false;
        }

        left ??= right;
        right ??= left;
        float amount = before.ReceivedAtSeconds >= after.ReceivedAtSeconds
            ? 0
            : (float)Math.Clamp(
                (target - before.ReceivedAtSeconds) / (after.ReceivedAtSeconds - before.ReceivedAtSeconds),
                0,
                1);

        Vec3 leftPosition = left!.Position ?? new Vec3();
        Vec3 rightPosition = right!.Position ?? new Vec3();
        Vec3 leftVelocity = left.Velocity ?? new Vec3();
        Vec3 rightVelocity = right.Velocity ?? new Vec3();
        sample = new SampledEntity(
            entityId,
            right.Kind,
            Lerp(leftPosition.X, rightPosition.X, amount),
            Lerp(leftPosition.Y, rightPosition.Y, amount),
            Lerp(leftPosition.Z, rightPosition.Z, amount),
            LerpAngle(left.Heading, right.Heading, amount),
            Lerp(leftVelocity.X, rightVelocity.X, amount),
            Lerp(leftVelocity.Y, rightVelocity.Y, amount),
            Lerp(leftVelocity.Z, rightVelocity.Z, amount),
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

    private static EntitySnapshot? Find(SnapshotBatch batch, ulong entityId)
    {
        return batch.Entities.FirstOrDefault(entity => entity.EntityId == entityId);
    }

    private static float Lerp(float left, float right, float amount) => left + ((right - left) * amount);

    private static float LerpAngle(float left, float right, float amount)
    {
        float delta = MathF.IEEERemainder(right - left, MathF.Tau);
        return left + (delta * amount);
    }

    private sealed record TimedSnapshot(SnapshotBatch Batch, double ReceivedAtSeconds);
}
