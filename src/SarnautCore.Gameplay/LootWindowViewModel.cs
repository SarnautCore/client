using Sarnaut.Protocol.V1;

namespace SarnautCore.Gameplay;

public sealed record LootEntryViewModel(string ItemId, int Count);

/// <summary>The fixed drop on one corpse and its all-or-nothing take command.</summary>
public sealed class LootWindowViewModel
{
    private readonly List<LootEntryViewModel> _items = [];

    public ulong CorpseEntityId { get; private set; }

    public long Money { get; private set; }

    public IReadOnlyList<LootEntryViewModel> Items => _items;

    public LootRefusal LastRefusal { get; private set; }

    public bool IsOpen { get; private set; }

    public bool IsEmpty => Money == 0 && _items.Count == 0;

    public event Action? Changed;

    public event Action<ulong>? TakeRequested;

    public event Action? Closed;

    public void Apply(LootOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        CorpseEntityId = offer.CorpseEntityId;
        Money = Math.Max(0, offer.Money);
        _items.Clear();
        foreach (LootItem item in offer.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.ItemId) && item.Count > 0)
            {
                _items.Add(new LootEntryViewModel(item.ItemId, item.Count));
            }
        }

        LastRefusal = LootRefusal.Unspecified;
        IsOpen = !IsEmpty;
        Changed?.Invoke();
        if (!IsOpen)
        {
            Closed?.Invoke();
        }
    }

    public bool RequestTake()
    {
        if (!IsOpen || IsEmpty || CorpseEntityId == 0)
        {
            return false;
        }

        TakeRequested?.Invoke(CorpseEntityId);
        return true;
    }

    public void Apply(LootResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.CorpseEntityId != CorpseEntityId)
        {
            return;
        }

        LastRefusal = result.Refusal;
        if (result.Refusal == LootRefusal.None)
        {
            Money = 0;
            _items.Clear();
        }

        Changed?.Invoke();
        if (IsEmpty)
        {
            Close();
        }
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        Changed?.Invoke();
        Closed?.Invoke();
    }
}
