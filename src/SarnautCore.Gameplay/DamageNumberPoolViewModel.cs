namespace SarnautCore.Gameplay;

/// <summary>One reusable floating combat number.</summary>
public sealed class DamageNumberViewModel
{
    public int PoolIndex { get; internal init; }

    public ulong EntityId { get; internal set; }

    public int Amount { get; internal set; }

    public bool Critical { get; internal set; }

    public bool Active { get; internal set; }

    public double RemainingSeconds { get; internal set; }

    internal long Sequence { get; set; }
}

/// <summary>A fixed pool of floating damage-number state.</summary>
public sealed class DamageNumberPoolViewModel
{
    private readonly DamageNumberViewModel[] _slots;
    private readonly double _lifetimeSeconds;
    private long _sequence;

    public DamageNumberPoolViewModel(int capacity = 48, double lifetimeSeconds = 1.2)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (lifetimeSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));
        }

        _lifetimeSeconds = lifetimeSeconds;
        _slots = Enumerable.Range(0, capacity)
            .Select(index => new DamageNumberViewModel { PoolIndex = index })
            .ToArray();
    }

    public IReadOnlyList<DamageNumberViewModel> Slots => _slots;

    public int ActiveCount { get; private set; }

    public event Action<DamageNumberViewModel>? Spawned;

    public event Action<DamageNumberViewModel>? Expired;

    public DamageNumberViewModel Spawn(ulong entityId, int amount, bool critical)
    {
        DamageNumberViewModel? slot = _slots.FirstOrDefault(candidate => !candidate.Active);
        if (slot is null)
        {
            slot = _slots.MinBy(candidate => candidate.Sequence)!;
            Deactivate(slot);
        }

        slot.EntityId = entityId;
        slot.Amount = Math.Max(0, amount);
        slot.Critical = critical;
        slot.RemainingSeconds = _lifetimeSeconds;
        slot.Sequence = ++_sequence;
        slot.Active = true;
        ActiveCount++;
        Spawned?.Invoke(slot);
        return slot;
    }

    public void Advance(double deltaSeconds)
    {
        if (deltaSeconds <= 0 || ActiveCount == 0)
        {
            return;
        }

        foreach (DamageNumberViewModel slot in _slots)
        {
            if (!slot.Active)
            {
                continue;
            }

            slot.RemainingSeconds = Math.Max(0, slot.RemainingSeconds - deltaSeconds);
            if (slot.RemainingSeconds == 0)
            {
                Deactivate(slot);
            }
        }
    }

    private void Deactivate(DamageNumberViewModel slot)
    {
        if (!slot.Active)
        {
            return;
        }

        slot.Active = false;
        slot.RemainingSeconds = 0;
        ActiveCount--;
        Expired?.Invoke(slot);
    }
}
