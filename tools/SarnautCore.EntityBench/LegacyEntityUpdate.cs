using Sarnaut.Protocol.V1;

namespace SarnautCore.EntityBench;

/// <summary>
/// The zone's entity update as it stood at <c>3badf4c</c>, kept verbatim so the
/// measurement has a real before and not a remembered one.
/// </summary>
/// <remarks>
/// Three costs compound here and all three are per frame:
/// <list type="bullet">
/// <item><c>LatestEntityIds</c> projects and copies a fresh array, and the loop
/// reads it twice.</item>
/// <item><c>TrySample</c> walks the timeline and then scans both batches with
/// <c>FirstOrDefault</c>, once per entity.</item>
/// <item>the stale sweep calls <c>Contains</c> on that array inside a
/// <c>Where</c>, which is a second full scan per tracked entity.</item>
/// </list>
/// </remarks>
internal sealed class LegacyEntityUpdate
{
    private readonly LegacySnapshotTimeline _timeline;
    private readonly Dictionary<ulong, FakeNode> _remoteEntities = [];

    internal LegacyEntityUpdate(LegacySnapshotTimeline timeline) => _timeline = timeline;

    internal int Count => _remoteEntities.Count;

    internal void UpdateEntities(double now, double interpolationDelaySeconds, ulong ownEntityId)
    {
        IReadOnlyCollection<ulong> latestIds = _timeline.LatestEntityIds;
        foreach (ulong entityId in latestIds)
        {
            if (!_timeline.TrySample(entityId, now, interpolationDelaySeconds, out LegacySampledEntity sample))
            {
                continue;
            }

            if (entityId == ownEntityId)
            {
                continue;
            }

            if (!_remoteEntities.TryGetValue(entityId, out FakeNode? entityNode))
            {
                entityNode = new FakeNode(entityId);
                _remoteEntities.Add(entityId, entityNode);
            }

            entityNode.X = sample.X;
            entityNode.Y = sample.Z;
            entityNode.Z = sample.Y;
            entityNode.Heading = sample.Heading;
        }

        foreach (ulong staleId in _remoteEntities.Keys.Where(id => !latestIds.Contains(id)).ToArray())
        {
            _remoteEntities.Remove(staleId);
        }
    }

    /// <summary>Stands in for the <c>Node3D</c> the zone moved.</summary>
    internal sealed class FakeNode(ulong entityId)
    {
        internal ulong EntityId { get; } = entityId;

        internal float X { get; set; }

        internal float Y { get; set; }

        internal float Z { get; set; }

        internal float Heading { get; set; }
    }
}

internal readonly record struct LegacySampledEntity(
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

/// <summary>The timeline as it stood at <c>3badf4c</c>, chunk reassembly included.</summary>
internal sealed class LegacySnapshotTimeline(int capacity = 32)
{
    private readonly int _capacity = capacity > 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly List<TimedSnapshot> _snapshots = [];
    private readonly Dictionary<uint, SnapshotBatch> _pending = [];
    private ulong _pendingTick;

    internal ulong LatestServerTick => _snapshots.Count == 0 ? 0 : _snapshots[^1].Batch.ServerTick;

    internal IReadOnlyCollection<ulong> LatestEntityIds => _snapshots.Count == 0
        ? Array.Empty<ulong>()
        : _snapshots[^1].Batch.Entities.Select(entity => entity.EntityId).ToArray();

    internal void Add(SnapshotBatch batch, double receivedAtSeconds)
    {
        ArgumentNullException.ThrowIfNull(batch);
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

    private SnapshotBatch? Reassemble(SnapshotBatch batch)
    {
        if (batch.ChunkCount <= 1)
        {
            _pending.Clear();
            return batch.Clone();
        }

        if (_pending.Count > 0 && batch.ServerTick < _pendingTick)
        {
            return null;
        }

        if (_pending.Count == 0 || batch.ServerTick != _pendingTick)
        {
            _pending.Clear();
            _pendingTick = batch.ServerTick;
        }

        if (batch.ChunkIndex >= batch.ChunkCount)
        {
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

    internal bool TrySample(
        ulong entityId,
        double nowSeconds,
        double interpolationDelaySeconds,
        out LegacySampledEntity sample)
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
        sample = new LegacySampledEntity(
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
