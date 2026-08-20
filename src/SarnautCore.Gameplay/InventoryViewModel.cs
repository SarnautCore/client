using Sarnaut.Protocol.V1;

namespace SarnautCore.Gameplay;

public enum InventoryRejection
{
    None,
    InvalidItem,
    Capacity,
}

public sealed class InventoryStackViewModel(string itemId, int count)
{
    public string ItemId { get; } = itemId;

    public int Count { get; internal set; } = count;
}

/// <summary>A fixed-capacity bag with atomic stack insertion and slot moves.</summary>
public sealed class InventoryViewModel
{
    private readonly InventoryStackViewModel?[] _slots;
    private readonly IReadOnlyList<InventoryStackViewModel?> _slotView;
    private readonly Func<string, int> _stackLimit;

    public InventoryViewModel(int capacity = 16, Func<string, int>? stackLimit = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _slots = new InventoryStackViewModel?[capacity];
        _slotView = Array.AsReadOnly(_slots);
        _stackLimit = stackLimit ?? (_ => 1);
    }

    public int Capacity => _slots.Length;

    public IReadOnlyList<InventoryStackViewModel?> Slots => _slotView;

    public long Currency { get; private set; }

    public int OccupiedSlots => _slots.Count(slot => slot is not null);

    public event Action? Changed;

    public bool TryAdd(string itemId, int count, out InventoryRejection rejection)
    {
        int limit = string.IsNullOrWhiteSpace(itemId) ? 0 : _stackLimit(itemId);
        if (count <= 0 || limit <= 0)
        {
            rejection = InventoryRejection.InvalidItem;
            return false;
        }

        int mergeCapacity = 0;
        int freeSlots = 0;
        foreach (InventoryStackViewModel? slot in _slots)
        {
            if (slot is null)
            {
                freeSlots++;
            }
            else if (slot.ItemId == itemId && slot.Count < limit)
            {
                mergeCapacity += limit - slot.Count;
            }
        }

        int afterMerges = Math.Max(0, count - mergeCapacity);
        int requiredSlots = (afterMerges + limit - 1) / limit;
        if (requiredSlots > freeSlots)
        {
            rejection = InventoryRejection.Capacity;
            return false;
        }

        int remaining = count;
        foreach (InventoryStackViewModel? slot in _slots)
        {
            if (slot is null || slot.ItemId != itemId || slot.Count >= limit)
            {
                continue;
            }

            int moved = Math.Min(limit - slot.Count, remaining);
            slot.Count += moved;
            remaining -= moved;
            if (remaining == 0)
            {
                break;
            }
        }

        for (int index = 0; index < _slots.Length && remaining > 0; index++)
        {
            if (_slots[index] is not null)
            {
                continue;
            }

            int stackCount = Math.Min(limit, remaining);
            _slots[index] = new InventoryStackViewModel(itemId, stackCount);
            remaining -= stackCount;
        }

        rejection = InventoryRejection.None;
        Changed?.Invoke();
        return true;
    }

    public bool TryMove(int fromSlot, int toSlot)
    {
        if (fromSlot < 0 || fromSlot >= _slots.Length
            || toSlot < 0 || toSlot >= _slots.Length
            || fromSlot == toSlot
            || _slots[fromSlot] is not { } source)
        {
            return false;
        }

        InventoryStackViewModel? destination = _slots[toSlot];
        if (destination is null)
        {
            _slots[toSlot] = source;
            _slots[fromSlot] = null;
        }
        else if (destination.ItemId == source.ItemId)
        {
            int limit = Math.Max(1, _stackLimit(source.ItemId));
            int moved = Math.Min(limit - destination.Count, source.Count);
            if (moved <= 0)
            {
                return false;
            }

            destination.Count += moved;
            source.Count -= moved;
            if (source.Count == 0)
            {
                _slots[fromSlot] = null;
            }
        }
        else
        {
            _slots[fromSlot] = destination;
            _slots[toSlot] = source;
        }

        Changed?.Invoke();
        return true;
    }

    public void Apply(InventoryUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Array.Clear(_slots);
        foreach (InventorySlot slot in update.Slots)
        {
            if (slot.Slot < 0 || slot.Slot >= _slots.Length || string.IsNullOrWhiteSpace(slot.ItemId) || slot.Count <= 0)
            {
                continue;
            }

            _slots[slot.Slot] = new InventoryStackViewModel(slot.ItemId, slot.Count);
        }

        Currency = Math.Max(0, update.Currency);
        Changed?.Invoke();
    }
}
