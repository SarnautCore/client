using Sarnaut.Protocol.V1;

namespace SarnautCore.Networking;

/// <summary>
/// The scene-side representation of one replicated entity: a model or a
/// placeholder, its nameplate, and the body a pick ray can hit.
/// </summary>
/// <remarks>
/// The registry is the client's one answer to "which entity is that", so it has
/// to own the visual's lifetime; it stays behind this interface so the answer is
/// testable without a Godot runtime.
/// </remarks>
public interface IEntityVisual
{
    /// <summary>
    /// Identifies the collision body a pick ray reports for this entity. The
    /// picker hands back whatever the ray hit and the registry turns it into an
    /// entity id, so this is the id of the body itself and not of its owner.
    /// </summary>
    ulong PickKey { get; }

    /// <summary>Moves and redresses the visual for a newly sampled state.</summary>
    void Apply(SampledEntity sample);

    /// <summary>The entity left the snapshot; free the scene nodes.</summary>
    void Retire();
}

/// <summary>Builds the visual for an entity the client has not seen before.</summary>
public interface IEntityVisualFactory
{
    IEntityVisual Create(SampledEntity sample);
}

/// <summary>One replicated entity, its last sampled state and its visual.</summary>
public sealed class TrackedEntity
{
    internal TrackedEntity(ulong entityId, IEntityVisual visual, SampledEntity sample, int stamp)
    {
        EntityId = entityId;
        Visual = visual;
        Latest = sample;
        Stamp = stamp;
    }

    public ulong EntityId { get; }

    public IEntityVisual Visual { get; }

    /// <summary>The most recent sample applied to the visual.</summary>
    public SampledEntity Latest { get; internal set; }

    /// <summary>The reconcile pass that last saw this entity.</summary>
    internal int Stamp { get; set; }
}

/// <summary>
/// Every entity the shard replicates to this client, keyed by entity id and by
/// the body a pick ray hits.
/// </summary>
/// <remarks>
/// <para>
/// The zone used to keep server entities in a private dictionary inside the
/// network loop, which meant nothing else in the scene could answer "what is
/// entity 41" or "what is under the cursor". Both questions are the registry's,
/// and both are answered in constant time.
/// </para>
/// <para>
/// The local player is deliberately not tracked: it is drawn by the controller
/// the player steers, so it has no registry visual to collide with the
/// controller's own. Its sample is published as <see cref="LocalSample"/>.
/// </para>
/// </remarks>
public sealed class EntityRegistry(IEntityVisualFactory factory)
{
    private readonly IEntityVisualFactory _factory = factory
        ?? throw new ArgumentNullException(nameof(factory));
    private readonly Dictionary<ulong, TrackedEntity> _entities = [];
    private readonly Dictionary<ulong, TrackedEntity> _byPickKey = [];

    /// <summary>
    /// Scratch space for the ids a reconcile pass has to drop. Reused rather
    /// than allocated: a dictionary cannot be written to while it is iterated,
    /// and a per-frame array of stale ids was half the old loop's garbage.
    /// </summary>
    private readonly List<ulong> _stale = [];
    private int _stamp;

    public int Count => _entities.Count;

    /// <summary>Every tracked entity id. Iterating this allocates nothing.</summary>
    public Dictionary<ulong, TrackedEntity>.KeyCollection Ids => _entities.Keys;

    /// <summary>The local player's last sample, once one tick has carried it.</summary>
    public SampledEntity LocalSample { get; private set; }

    public bool HasLocalSample { get; private set; }

    public bool TryGet(ulong entityId, out TrackedEntity entity) =>
        _entities.TryGetValue(entityId, out entity!);

    /// <summary>
    /// Resolves the body a pick ray hit back to the entity that owns it.
    /// </summary>
    public bool TryGetByPickKey(ulong pickKey, out TrackedEntity entity) =>
        _byPickKey.TryGetValue(pickKey, out entity!);

    public bool Contains(ulong entityId) => _entities.ContainsKey(entityId);

    /// <summary>
    /// Brings the registry in line with one snapshot window: spawns what is new,
    /// applies what moved, and retires what the shard stopped sending.
    /// </summary>
    /// <remarks>
    /// This runs every frame at whatever entity count the shard subscribes the
    /// client to, so it allocates only when an entity is genuinely new. Staleness
    /// is a stamp comparison rather than a membership test against the tick's id
    /// list, which is what made the old prune quadratic.
    /// </remarks>
    public void Reconcile(SnapshotWindow window, ulong localEntityId)
    {
        if (window.IsEmpty)
        {
            return;
        }

        unchecked
        {
            _stamp++;
        }

        foreach (ulong entityId in window.EntityIds)
        {
            if (!window.TrySample(entityId, out SampledEntity sample))
            {
                continue;
            }

            if (entityId == localEntityId)
            {
                LocalSample = sample;
                HasLocalSample = true;
                continue;
            }

            Upsert(sample);
        }

        Prune();
    }

    /// <summary>
    /// Adds an entity, or re-dresses one already tracked, and returns it.
    /// </summary>
    public TrackedEntity Upsert(SampledEntity sample)
    {
        if (_entities.TryGetValue(sample.EntityId, out TrackedEntity? tracked))
        {
            tracked.Latest = sample;
            tracked.Stamp = _stamp;
            tracked.Visual.Apply(sample);
            return tracked;
        }

        IEntityVisual visual = _factory.Create(sample);
        tracked = new TrackedEntity(sample.EntityId, visual, sample, _stamp);
        _entities.Add(sample.EntityId, tracked);
        _byPickKey[visual.PickKey] = tracked;
        visual.Apply(sample);
        return tracked;
    }

    /// <summary>Retires one entity and forgets it. Returns false if it was not tracked.</summary>
    public bool Remove(ulong entityId)
    {
        if (!_entities.Remove(entityId, out TrackedEntity? tracked))
        {
            return false;
        }

        _byPickKey.Remove(tracked.Visual.PickKey);
        tracked.Visual.Retire();
        return true;
    }

    public void Clear()
    {
        foreach (KeyValuePair<ulong, TrackedEntity> pair in _entities)
        {
            pair.Value.Visual.Retire();
        }

        _entities.Clear();
        _byPickKey.Clear();
        HasLocalSample = false;
        LocalSample = default;
    }

    /// <summary>
    /// Picks the next target in a Tab cycle: the nearest living candidate
    /// further away than the current one, wrapping to the nearest of all when
    /// the current target is the furthest or is gone.
    /// </summary>
    /// <remarks>
    /// Distance is measured in the shard's own axes, which is where the samples
    /// are. The scan is two linear passes with no sort, so a Tab press costs the
    /// same as a frame of reconcile.
    /// </remarks>
    public bool TryCycleTarget(
        ulong currentTargetId,
        float originX,
        float originY,
        float originZ,
        float maxRangeMetres,
        out ulong nextEntityId)
    {
        nextEntityId = 0;
        float maxRangeSquared = maxRangeMetres * maxRangeMetres;
        float currentDistanceSquared = -1;
        if (_entities.TryGetValue(currentTargetId, out TrackedEntity? current))
        {
            currentDistanceSquared = DistanceSquared(current.Latest, originX, originY, originZ);
        }

        float nearestSquared = float.MaxValue;
        ulong nearest = 0;
        float nextSquared = float.MaxValue;
        foreach (KeyValuePair<ulong, TrackedEntity> pair in _entities)
        {
            TrackedEntity candidate = pair.Value;
            if (!IsTargetable(candidate.Latest))
            {
                continue;
            }

            float distanceSquared = DistanceSquared(candidate.Latest, originX, originY, originZ);
            if (distanceSquared > maxRangeSquared)
            {
                continue;
            }

            if (distanceSquared < nearestSquared || (distanceSquared == nearestSquared && pair.Key < nearest))
            {
                nearestSquared = distanceSquared;
                nearest = pair.Key;
            }

            // Ties are broken by entity id so that two mobs at the same distance
            // are still two stops of the cycle rather than one.
            bool after = distanceSquared > currentDistanceSquared
                || (distanceSquared == currentDistanceSquared && pair.Key > currentTargetId);
            if (!after)
            {
                continue;
            }

            if (distanceSquared < nextSquared || (distanceSquared == nextSquared && pair.Key < nextEntityId))
            {
                nextSquared = distanceSquared;
                nextEntityId = pair.Key;
            }
        }

        if (nextEntityId != 0)
        {
            return true;
        }

        nextEntityId = nearest;
        return nearest != 0;
    }

    private static bool IsTargetable(SampledEntity sample) => sample.Alive && sample.Kind != EntityKind.Unspecified;

    private static float DistanceSquared(SampledEntity sample, float x, float y, float z)
    {
        float dx = sample.X - x;
        float dy = sample.Y - y;
        float dz = sample.Z - z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private void Prune()
    {
        _stale.Clear();
        foreach (KeyValuePair<ulong, TrackedEntity> pair in _entities)
        {
            if (pair.Value.Stamp != _stamp)
            {
                _stale.Add(pair.Key);
            }
        }

        foreach (ulong entityId in _stale)
        {
            Remove(entityId);
        }
    }
}
