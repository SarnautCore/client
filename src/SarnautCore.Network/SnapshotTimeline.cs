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

    public ulong LatestServerTick => _snapshots.Count == 0 ? 0 : _snapshots[^1].Batch.ServerTick;

    public IReadOnlyCollection<ulong> LatestEntityIds => _snapshots.Count == 0
        ? Array.Empty<ulong>()
        : _snapshots[^1].Batch.Entities.Select(entity => entity.EntityId).ToArray();

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

        if (_snapshots.Count > 0 && batch.ServerTick == _snapshots[^1].Batch.ServerTick)
        {
            _snapshots[^1] = new TimedSnapshot(batch.Clone(), receivedAtSeconds);
        }
        else
        {
            _snapshots.Add(new TimedSnapshot(batch.Clone(), receivedAtSeconds));
        }

        if (_snapshots.Count > _capacity)
        {
            _snapshots.RemoveRange(0, _snapshots.Count - _capacity);
        }
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
