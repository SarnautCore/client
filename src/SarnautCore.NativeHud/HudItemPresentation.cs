namespace SarnautCore.NativeHud;

public enum HudItemQuality
{
    System = 0,
    Junk = 1,
    Goods = 2,
    Common = 3,
    Uncommon = 4,
    Rare = 5,
    Epic = 6,
}

public enum HudItemCategory
{
    Equipment = 0,
    Consumable = 1,
    Quest = 2,
    Junk = 3,
    Other = 4,
}

public enum HudEquipmentSlot
{
    None = 0,
    MainHand = 1,
    OffHand = 2,
    TwoHanded = 3,
    Ranged = 4,
    Armor = 5,
    Pants = 6,
    Boots = 7,
}

public readonly record struct HudRetailDressSlot(string Name, byte Ordinal)
{
    internal bool IsValid => (Name, Ordinal) is
        ("ARMOR", 1) or
        ("PANTS", 2) or
        ("BOOTS", 3) or
        ("MAINHAND", 14) or
        ("OFFHAND", 15) or
        ("RANGED", 16) or
        ("UNDRESSABLE", 26) or
        ("TWOHANDED", 27);
}

/// <summary>Static private-product presentation resolved by exact item identifier.</summary>
public readonly record struct HudItemPresentation(
    HudId ItemId,
    HudId NameTextId,
    HudId DescriptionTextId,
    HudId IconTextureId,
    HudItemQuality AuthoredQuality,
    HudItemCategory Category,
    HudEquipmentSlot EquipmentSlot,
    HudRetailDressSlot RetailDressSlot,
    int StackLimit,
    HudId? ActionId)
{
    internal bool IsValid => !ItemId.IsEmpty && !NameTextId.IsEmpty && !DescriptionTextId.IsEmpty &&
        !IconTextureId.IsEmpty && (uint)AuthoredQuality <= (uint)HudItemQuality.Epic &&
        (uint)Category <= (uint)HudItemCategory.Other &&
        (uint)EquipmentSlot <= (uint)HudEquipmentSlot.Boots && RetailDressSlot.IsValid &&
        StackLimit > 0 && ActionId is not { IsEmpty: true };
}

/// <summary>
/// Engine-neutral lookup over the compiled private item-presentation catalog. A miss returns
/// false; the adapter applies the catalog's own unknown presentation without fabricating data.
/// </summary>
public interface IHudItemPresentationCatalog
{
    bool TryGet(HudId itemId, out HudItemPresentation presentation);
}

/// <summary>Deterministic catalog adapter for tests, tools, and offline playback.</summary>
public sealed class RecordingHudItemPresentationCatalog : IHudItemPresentationCatalog
{
    private readonly HudItemPresentation[] _presentations;
    private readonly List<HudId> _lookups = [];

    public RecordingHudItemPresentationCatalog(HudItemPresentation[] presentations)
    {
        ArgumentNullException.ThrowIfNull(presentations);
        _presentations = (HudItemPresentation[])presentations.Clone();
        for (int index = 0; index < _presentations.Length; index++)
        {
            if (!_presentations[index].IsValid ||
                _presentations.AsSpan(0, index).Contains(_presentations[index]))
            {
                throw new ArgumentException("Item presentations must be valid and uniquely keyed.", nameof(presentations));
            }

            for (int earlier = 0; earlier < index; earlier++)
            {
                if (_presentations[earlier].ItemId == _presentations[index].ItemId)
                {
                    throw new ArgumentException("Item presentation identifiers must be unique.", nameof(presentations));
                }
            }
        }
    }

    public IReadOnlyList<HudId> Lookups => _lookups;

    public bool TryGet(HudId itemId, out HudItemPresentation presentation)
    {
        _lookups.Add(itemId);
        for (int index = 0; index < _presentations.Length; index++)
        {
            if (_presentations[index].ItemId == itemId)
            {
                presentation = _presentations[index];
                return true;
            }
        }

        presentation = default;
        return false;
    }
}
