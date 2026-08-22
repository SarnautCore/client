namespace SarnautCore.NativeHud;

public readonly record struct HudInventoryPartitionProduct(
    HudId Element,
    int FirstSlot,
    int SlotCount);

/// <summary>One of the ten concrete multibag panels authored by the 1.1 product.</summary>
public sealed class HudInventoryLayoutProduct
{
    private readonly HudId[] _slots;
    private readonly HudInventoryPartitionProduct[] _partitions;

    public HudInventoryLayoutProduct(HudId element, int capacity, HudId[] slots, HudInventoryPartitionProduct[] partitions)
    {
        if (element.IsEmpty)
        {
            throw new ArgumentException("An inventory layout needs a semantic element.", nameof(element));
        }

        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(partitions);
        if (capacity <= 0 || slots.Length != capacity || partitions.Length is <= 0 or > HudProduct.InventoryPartitionCount)
        {
            throw new ArgumentException("An inventory layout must bind every slot and one to five authored bags.");
        }

        _slots = (HudId[])slots.Clone();
        _partitions = (HudInventoryPartitionProduct[])partitions.Clone();
        ValidateIds(_slots, nameof(slots));

        int nextSlot = 0;
        for (int index = 0; index < _partitions.Length; index++)
        {
            HudInventoryPartitionProduct bag = _partitions[index];
            if (bag.Element.IsEmpty || bag.FirstSlot != nextSlot || bag.SlotCount <= 0 ||
                bag.FirstSlot + bag.SlotCount > capacity)
            {
                throw new ArgumentException("Inventory partitions must be contiguous and cover the layout.", nameof(partitions));
            }

            nextSlot += bag.SlotCount;
        }

        if (nextSlot != capacity)
        {
            throw new ArgumentException("Inventory partitions must cover the layout.", nameof(partitions));
        }

        Element = element;
        Capacity = capacity;
    }

    public HudId Element { get; }

    public int Capacity { get; }

    public ReadOnlySpan<HudId> Slots => _slots;

    public ReadOnlySpan<HudInventoryPartitionProduct> Partitions => _partitions;

    internal HudInventoryLayoutProduct Clone() =>
        new(Element, Capacity, _slots, _partitions);

    private static void ValidateIds(HudId[] values, string parameterName)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index].IsEmpty)
            {
                throw new ArgumentException("Inventory slot elements cannot be empty.", parameterName);
            }

            for (int earlier = 0; earlier < index; earlier++)
            {
                if (values[earlier] == values[index])
                {
                    throw new ArgumentException($"Inventory slot element '{values[index]}' is duplicated.", parameterName);
                }
            }
        }
    }
}

public sealed class HudInventoryProduct
{
    private static readonly int[] AuthoredCapacities = [12, 16, 18, 24, 30, 36, 42, 48, 54, 60];
    private static readonly int[][] AuthoredPartitions =
    [
        [12], [16], [12, 6], [16, 8], [30], [8, 8, 8, 6, 6], [30, 12],
        [12, 12, 12, 12], [30, 12, 12], [30, 30],
    ];
    private readonly HudInventoryLayoutProduct[] _layouts;

    public HudInventoryProduct(HudId root, HudInventoryLayoutProduct[] layouts)
    {
        if (root.IsEmpty)
        {
            throw new ArgumentException("The multibag root is required.", nameof(root));
        }

        ArgumentNullException.ThrowIfNull(layouts);
        if (layouts.Length != HudProduct.InventoryLayoutCount)
        {
            throw new ArgumentException($"Multibag must contain exactly {HudProduct.InventoryLayoutCount} authored layouts.", nameof(layouts));
        }

        _layouts = new HudInventoryLayoutProduct[layouts.Length];
        for (int index = 0; index < layouts.Length; index++)
        {
            if (layouts[index].Capacity != AuthoredCapacities[index])
            {
                throw new ArgumentException("Multibag layout capacities must match the authored 1.1 sequence.", nameof(layouts));
            }

            ReadOnlySpan<HudInventoryPartitionProduct> bags = layouts[index].Partitions;
            if (bags.Length != AuthoredPartitions[index].Length)
            {
                throw new ArgumentException("Multibag partition counts must match the authored 1.1 layouts.", nameof(layouts));
            }

            for (int bag = 0; bag < bags.Length; bag++)
            {
                if (bags[bag].SlotCount != AuthoredPartitions[index][bag])
                {
                    throw new ArgumentException("Multibag partition sizes must match the authored 1.1 layouts.", nameof(layouts));
                }
            }

            _layouts[index] = layouts[index].Clone();
        }

        Root = root;
    }

    public HudId Root { get; }

    public ReadOnlySpan<HudInventoryLayoutProduct> Layouts => _layouts;

    internal HudInventoryLayoutProduct FindLayout(int capacity)
    {
        for (int index = 0; index < _layouts.Length; index++)
        {
            if (_layouts[index].Capacity == capacity)
            {
                return _layouts[index];
            }
        }

        throw new ArgumentOutOfRangeException(nameof(capacity), "The inventory capacity has no authored multibag layout.");
    }

    internal bool TryFindLayout(int capacity, out HudInventoryLayoutProduct? layout)
    {
        for (int index = 0; index < _layouts.Length; index++)
        {
            if (_layouts[index].Capacity == capacity)
            {
                layout = _layouts[index];
                return true;
            }
        }

        layout = null;
        return false;
    }
}

public sealed class HudLootProduct
{
    private readonly HudId[] _pageSlots;

    public HudLootProduct(HudId root, HudId[] pageSlots, int maxEntries = 20)
    {
        if (root.IsEmpty)
        {
            throw new ArgumentException("The loot root is required.", nameof(root));
        }

        ArgumentNullException.ThrowIfNull(pageSlots);
        if (pageSlots.Length != HudProduct.LootPageSize || maxEntries != HudProduct.LootEntryCount)
        {
            throw new ArgumentException($"Loot must bind {HudProduct.LootPageSize} authored page slots and the exact {HudProduct.LootEntryCount}-entry retail bound.");
        }

        _pageSlots = (HudId[])pageSlots.Clone();
        ValidateUnique(_pageSlots, nameof(pageSlots));
        Root = root;
        MaxEntries = maxEntries;
    }

    public HudId Root { get; }

    public ReadOnlySpan<HudId> PageSlots => _pageSlots;

    public int MaxEntries { get; }

    private static void ValidateUnique(HudId[] values, string parameterName)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index].IsEmpty || values.AsSpan(0, index).Contains(values[index]))
            {
                throw new ArgumentException("Loot page slot elements must be non-empty and unique.", parameterName);
            }
        }
    }
}

public sealed class HudQuestLogProduct
{
    private readonly HudId[] _entries;
    private readonly HudId[] _bookmarks;
    private readonly HudId[] _objectives;
    private readonly HudId[] _alternativeRewards;
    private readonly HudId[] _mandatoryRewards;
    private readonly HudId[] _reputations;
    private readonly HudId[] _currencies;
    private readonly HudId[] _secretComponents;

    public HudQuestLogProduct(
        HudId root,
        HudId[] entries,
        HudId[] bookmarks,
        HudId[] objectives,
        HudId[] alternativeRewards,
        HudId[] mandatoryRewards,
        HudId[] reputations,
        HudId[] currencies,
        HudId[] secretComponents)
    {
        if (root.IsEmpty)
        {
            throw new ArgumentException("The quest-log root is required.", nameof(root));
        }

        _entries = CloneExact(entries, HudProduct.QuestLogEntryCount, nameof(entries));
        _bookmarks = CloneExact(bookmarks, HudProduct.QuestLogBookmarkCount, nameof(bookmarks));
        _objectives = CloneExact(objectives, HudProduct.QuestLogObjectiveCount, nameof(objectives));
        _alternativeRewards = CloneExact(alternativeRewards, HudProduct.QuestInfoRewardItemCount, nameof(alternativeRewards));
        _mandatoryRewards = CloneExact(mandatoryRewards, HudProduct.QuestInfoRewardItemCount, nameof(mandatoryRewards));
        _reputations = CloneExact(reputations, HudProduct.QuestInfoReputationCount, nameof(reputations));
        _currencies = CloneExact(currencies, HudProduct.QuestInfoCurrencyCount, nameof(currencies));
        _secretComponents = CloneExact(secretComponents, HudProduct.QuestLogSecretComponentCount, nameof(secretComponents));
        Root = root;
    }

    public HudId Root { get; }
    public ReadOnlySpan<HudId> Entries => _entries;
    public ReadOnlySpan<HudId> Bookmarks => _bookmarks;
    public ReadOnlySpan<HudId> Objectives => _objectives;
    public ReadOnlySpan<HudId> AlternativeRewards => _alternativeRewards;
    public ReadOnlySpan<HudId> MandatoryRewards => _mandatoryRewards;
    public ReadOnlySpan<HudId> Reputations => _reputations;
    public ReadOnlySpan<HudId> Currencies => _currencies;
    public ReadOnlySpan<HudId> SecretComponents => _secretComponents;
    public int MaxEntries => _entries.Length;

    private static HudId[] CloneExact(HudId[] values, int expected, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != expected)
        {
            throw new ArgumentException($"The authored role census requires exactly {expected} entries.", parameterName);
        }

        var result = (HudId[])values.Clone();
        ValidateSemanticIds(result, parameterName);
        return result;
    }

    private static void ValidateSemanticIds(HudId[] values, string parameterName)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index].IsEmpty || values.AsSpan(0, index).Contains(values[index]))
            {
                throw new ArgumentException("Semantic elements must be non-empty and unique within their authored pool.", parameterName);
            }
        }
    }
}

public sealed class HudQuestInfoProduct
{
    private readonly HudId[] _talkOptions;
    private readonly HudId[] _objectives;
    private readonly HudId[] _alternativeRewards;
    private readonly HudId[] _mandatoryRewards;
    private readonly HudId[] _reputations;
    private readonly HudId[] _currencies;

    public HudQuestInfoProduct(
        HudId detailRoot,
        HudId interactionRoot,
        HudId[] talkOptions,
        HudId[] objectives,
        HudId[] alternativeRewards,
        HudId[] mandatoryRewards,
        HudId[] reputations,
        HudId[] currencies)
    {
        if (detailRoot.IsEmpty || interactionRoot.IsEmpty || detailRoot == interactionRoot)
        {
            throw new ArgumentException("Distinct quest-info detail and NPC-talk interaction roots are required.");
        }

        _talkOptions = CloneExact(talkOptions, HudProduct.QuestTalkOptionCount, nameof(talkOptions));
        _objectives = CloneExact(objectives, HudProduct.QuestInfoObjectiveCount, nameof(objectives));
        _alternativeRewards = CloneExact(alternativeRewards, HudProduct.QuestInfoRewardItemCount, nameof(alternativeRewards));
        _mandatoryRewards = CloneExact(mandatoryRewards, HudProduct.QuestInfoRewardItemCount, nameof(mandatoryRewards));
        _reputations = CloneExact(reputations, HudProduct.QuestInfoReputationCount, nameof(reputations));
        _currencies = CloneExact(currencies, HudProduct.QuestInfoCurrencyCount, nameof(currencies));
        DetailRoot = detailRoot;
        InteractionRoot = interactionRoot;
    }

    public HudId DetailRoot { get; }
    public HudId InteractionRoot { get; }
    public ReadOnlySpan<HudId> TalkOptions => _talkOptions;
    public ReadOnlySpan<HudId> Objectives => _objectives;
    public ReadOnlySpan<HudId> AlternativeRewards => _alternativeRewards;
    public ReadOnlySpan<HudId> MandatoryRewards => _mandatoryRewards;
    public ReadOnlySpan<HudId> Reputations => _reputations;
    public ReadOnlySpan<HudId> Currencies => _currencies;

    internal int MaxDynamicEntries => _objectives.Length + _alternativeRewards.Length +
        _mandatoryRewards.Length + _reputations.Length + _currencies.Length;

    private static HudId[] CloneExact(HudId[] values, int expected, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != expected)
        {
            throw new ArgumentException($"The authored role census requires exactly {expected} entries.", parameterName);
        }

        var result = (HudId[])values.Clone();
        for (int index = 0; index < result.Length; index++)
        {
            if (result[index].IsEmpty || result.AsSpan(0, index).Contains(result[index]))
            {
                throw new ArgumentException("Semantic elements must be non-empty and unique within their authored pool.", parameterName);
            }
        }

        return result;
    }
}

public sealed class HudCharacterProduct
{
    private readonly HudId[] _equipmentSlots;
    private readonly HudId[] _statRows;

    public HudCharacterProduct(HudId root, HudId[] equipmentSlots, HudId[] statRows)
    {
        if (root.IsEmpty)
        {
            throw new ArgumentException("The character root is required.");
        }

        ArgumentNullException.ThrowIfNull(equipmentSlots);
        ArgumentNullException.ThrowIfNull(statRows);
        if (equipmentSlots.Length != HudProduct.CharacterEquipmentSlotCount ||
            statRows.Length != HudProduct.CharacterStatCount)
        {
            throw new ArgumentException("Character bindings must match Slot01..Slot21 and the fourteen authored stats.");
        }

        _equipmentSlots = (HudId[])equipmentSlots.Clone();
        _statRows = (HudId[])statRows.Clone();
        ValidateUnique(_equipmentSlots, nameof(equipmentSlots));
        ValidateUnique(_statRows, nameof(statRows));
        Root = root;
    }

    public HudId Root { get; }

    public ReadOnlySpan<HudId> EquipmentSlots => _equipmentSlots;

    public HudId BagSlot => _equipmentSlots[HudProduct.CharacterBagSlot];

    public HudId DeathInsuranceSlot => _equipmentSlots[HudProduct.CharacterDeathInsuranceSlot];

    public ReadOnlySpan<HudId> StatRows => _statRows;

    private static void ValidateUnique(HudId[] values, string parameterName)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index].IsEmpty || values.AsSpan(0, index).Contains(values[index]))
            {
                throw new ArgumentException("Character semantic elements must be non-empty and unique.", parameterName);
            }
        }
    }
}

/// <summary>Validated semantic bindings for gameplay contexts and the one shared modal surface.</summary>
public sealed class HudContextProduct
{
    public HudContextProduct(
        HudInventoryProduct inventory,
        HudLootProduct loot,
        HudQuestLogProduct questLog,
        HudQuestInfoProduct questInfo,
        HudCharacterProduct character,
        HudMessageBoxProduct messageBox)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(loot);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(questLog);
        ArgumentNullException.ThrowIfNull(questInfo);
        ArgumentNullException.ThrowIfNull(messageBox);
        Inventory = inventory;
        Loot = loot;
        QuestLog = questLog;
        QuestInfo = questInfo;
        Character = character;
        MessageBox = messageBox;
    }

    public HudInventoryProduct Inventory { get; }

    public HudLootProduct Loot { get; }

    public HudQuestLogProduct QuestLog { get; }

    public HudQuestInfoProduct QuestInfo { get; }

    public HudCharacterProduct Character { get; }

    public HudMessageBoxProduct MessageBox { get; }
}
