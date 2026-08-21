namespace SarnautCore.NativeHud;

/// <summary>
/// Authoritative mutable item-instance state. Static name, icon, quality, category, and equip
/// rules are resolved from the private compiled item catalog by <see cref="ItemId"/>.
/// </summary>
public readonly record struct HudItemReference(HudId ItemId, ulong InstanceId)
{
    internal bool IsValid => !ItemId.IsEmpty && InstanceId != 0;
}

public readonly record struct HudItemStack(
    HudId ItemId,
    int Count,
    ulong InstanceId,
    int CounterValue = 0,
    bool Bound = false,
    bool Cursed = false,
    bool IsQuestOperator = false,
    long RemoveTime = 0,
    HudId RuneId = default,
    HudId RuneSlotId = default)
{
    internal bool IsValid => !ItemId.IsEmpty && InstanceId != 0 && Count > 0 && CounterValue >= 0;
}

public readonly record struct HudRewardItem(HudId ItemId, int Count)
{
    internal bool IsValid => !ItemId.IsEmpty && Count > 0;
}

public readonly record struct HudInventoryCooldown(
    HudId SpellId,
    int RemainingMilliseconds,
    int DurationMilliseconds)
{
    internal bool IsValid => !SpellId.IsEmpty && RemainingMilliseconds > 0 &&
        DurationMilliseconds >= RemainingMilliseconds;
}

/// <summary>One complete authoritative bag replacement.</summary>
public sealed class HudInventorySnapshot
{
    private readonly HudItemStack?[] _slots;
    private readonly HudInventoryCooldown?[] _cooldowns;

    public HudInventorySnapshot(
        int capacity,
        long currency,
        HudItemReference equippedBag,
        HudItemStack?[] slots,
        HudInventoryCooldown?[]? cooldowns = null)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (capacity <= 0 || slots.Length != capacity || currency < 0 || !equippedBag.IsValid)
        {
            throw new ArgumentException("Inventory snapshots need an equipped bag, purse, capacity, and one value per visible slot.");
        }

        _slots = (HudItemStack?[])slots.Clone();
        _cooldowns = cooldowns is null ? new HudInventoryCooldown?[capacity] :
            (HudInventoryCooldown?[])cooldowns.Clone();
        if (_cooldowns.Length != capacity)
        {
            throw new ArgumentException("Inventory cooldown state must align one-for-one with the flat slots.", nameof(cooldowns));
        }

        for (int index = 0; index < _slots.Length; index++)
        {
            if (_slots[index] is { } stack && !stack.IsValid)
            {
                throw new ArgumentException("Inventory stacks need an item and a positive count.", nameof(slots));
            }

            if (_slots[index] is { } item &&
                (item.InstanceId == equippedBag.InstanceId || HasEarlierInstance(_slots, index, item.InstanceId)))
            {
                throw new ArgumentException("An inventory item instance cannot occupy multiple slots or the equipped-bag role.", nameof(slots));
            }

            if (_cooldowns[index] is { } cooldown && (!cooldown.IsValid || _slots[index] is null))
            {
                throw new ArgumentException("Inventory cooldowns require an occupied slot, spell, and valid remaining duration.", nameof(cooldowns));
            }
        }

        Capacity = capacity;
        Currency = currency;
        EquippedBag = equippedBag;
    }

    public int Capacity { get; }

    public long Currency { get; }

    public HudItemReference EquippedBag { get; }

    public ReadOnlySpan<HudItemStack?> Slots => _slots;

    public ReadOnlySpan<HudInventoryCooldown?> Cooldowns => _cooldowns;

    internal bool ContentEquals(HudInventorySnapshot other) =>
        Capacity == other.Capacity && Currency == other.Currency && EquippedBag == other.EquippedBag &&
        Slots.SequenceEqual(other.Slots) && Cooldowns.SequenceEqual(other.Cooldowns);

    private static bool HasEarlierInstance(HudItemStack?[] items, int index, ulong instanceId)
    {
        for (int earlier = 0; earlier < index; earlier++)
        {
            if (items[earlier]?.InstanceId == instanceId)
            {
                return true;
            }
        }

        return false;
    }
}

public enum HudLootRefusal
{
    None,
    BagFull,
    NotOwner,
    OutOfRange,
    Unavailable,
}

public readonly record struct HudLootItem(HudId ItemId, int Count, bool Cursed = false)
{
    internal bool IsValid => !ItemId.IsEmpty && Count > 0;
}

/// <summary>The complete fixed drop behind one corpse. Paging remains client-local.</summary>
public sealed class HudLootSnapshot
{
    private readonly HudLootItem[] _items;

    public HudLootSnapshot(
        ulong corpseEntityId,
        long money,
        HudLootItem[] items,
        HudLootRefusal refusal = HudLootRefusal.None,
        bool open = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (money < 0 || (open && corpseEntityId == 0))
        {
            throw new ArgumentException("An open loot bag needs a corpse and non-negative money.");
        }

        _items = (HudLootItem[])items.Clone();
        if (_items.Any(item => !item.IsValid))
        {
            throw new ArgumentException("Loot entries need an item and a positive count.", nameof(items));
        }

        CorpseEntityId = corpseEntityId;
        Money = money;
        Refusal = refusal;
        Open = open && (money > 0 || _items.Length > 0 || refusal != HudLootRefusal.None);
    }

    public ulong CorpseEntityId { get; }

    public long Money { get; }

    public HudLootRefusal Refusal { get; }

    public bool Open { get; }

    public ReadOnlySpan<HudLootItem> Items => _items;

    internal bool ContentEquals(HudLootSnapshot other) =>
        CorpseEntityId == other.CorpseEntityId && Money == other.Money && Refusal == other.Refusal &&
        Open == other.Open && Items.SequenceEqual(other.Items);
}

public enum HudQuestClientState
{
    Unavailable,
    Offered,
    Accepted,
    InProgress,
    Completable,
    TurnedIn,
    Abandoned,
    Failed,
}

public sealed class HudQuestDocument
{
    private readonly HudQuestObjective[] _objectives;

    public HudQuestDocument(
        HudId questId,
        HudId titleId,
        HudId descriptionId,
        HudQuestClientState state,
        bool canAbandon,
        HudQuestObjective[] objectives,
        HudQuestRewardSnapshot? reward = null)
    {
        if (questId.IsEmpty || titleId.IsEmpty || descriptionId.IsEmpty)
        {
            throw new ArgumentException("Quest documents need stable quest, title, and description identifiers.");
        }

        ArgumentNullException.ThrowIfNull(objectives);
        _objectives = (HudQuestObjective[])objectives.Clone();
        for (int index = 0; index < _objectives.Length; index++)
        {
            HudQuestObjective objective = _objectives[index];
            if (objective.TextId.IsEmpty || objective.Current < 0 || objective.Required <= 0 ||
                objective.Current > objective.Required || (index > 0 && _objectives[index - 1].Index >= objective.Index))
            {
                throw new ArgumentException("Quest objectives must be valid and ordered by source index.", nameof(objectives));
            }
        }

        QuestId = questId;
        TitleId = titleId;
        DescriptionId = descriptionId;
        State = state;
        CanAbandon = canAbandon;
        Reward = reward ?? HudQuestRewardSnapshot.Empty;
    }

    public HudId QuestId { get; }

    public HudId TitleId { get; }

    public HudId DescriptionId { get; }

    public HudQuestClientState State { get; }

    public bool CanAbandon { get; }

    public HudQuestRewardSnapshot Reward { get; }

    public ReadOnlySpan<HudQuestObjective> Objectives => _objectives;

    internal bool ContentEquals(HudQuestDocument other) =>
        QuestId == other.QuestId && TitleId == other.TitleId && DescriptionId == other.DescriptionId &&
        State == other.State && CanAbandon == other.CanAbandon && Objectives.SequenceEqual(other.Objectives) &&
        Reward.ContentEquals(other.Reward);
}

public enum HudQuestLogBookmark
{
    Zones,
    Completed,
    WorldSecrets,
}

public sealed class HudQuestLogSnapshot
{
    private readonly HudQuestDocument[] _quests;
    private readonly HudId[] _secretComponents;

    public HudQuestLogSnapshot(
        HudQuestDocument[] quests,
        HudQuestLogBookmark activeBookmark = HudQuestLogBookmark.Zones,
        HudId[]? secretComponents = null,
        HudQuestShareInvitation? shareInvitation = null)
    {
        ArgumentNullException.ThrowIfNull(quests);
        _quests = (HudQuestDocument[])quests.Clone();
        _secretComponents = secretComponents is null ? [] : (HudId[])secretComponents.Clone();
        if ((uint)activeBookmark >= HudProduct.QuestLogBookmarkCount ||
            _secretComponents.Length > HudProduct.QuestLogSecretComponentCount ||
            _secretComponents.Any(component => component.IsEmpty))
        {
            throw new ArgumentException("Quest-log bookmark or world-secret state exceeds the authored pools.");
        }
        for (int index = 0; index < _quests.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(_quests[index]);
            for (int earlier = 0; earlier < index; earlier++)
            {
                if (_quests[earlier].QuestId == _quests[index].QuestId)
                {
                    throw new ArgumentException("Quest log entries must have unique identifiers.", nameof(quests));
                }
            }
        }

        if (shareInvitation is { IsValid: false })
        {
            throw new ArgumentException("Quest-share invitations need share, quest, and sharer identifiers.", nameof(shareInvitation));
        }

        ShareInvitation = shareInvitation;
        ActiveBookmark = activeBookmark;
    }

    public ReadOnlySpan<HudQuestDocument> Quests => _quests;

    public HudQuestShareInvitation? ShareInvitation { get; }

    public HudQuestLogBookmark ActiveBookmark { get; }

    public ReadOnlySpan<HudId> SecretComponents => _secretComponents;

    internal bool ContentEquals(HudQuestLogSnapshot other)
    {
        if (_quests.Length != other._quests.Length || ShareInvitation != other.ShareInvitation ||
            ActiveBookmark != other.ActiveBookmark || !SecretComponents.SequenceEqual(other.SecretComponents))
        {
            return false;
        }

        for (int index = 0; index < _quests.Length; index++)
        {
            if (!_quests[index].ContentEquals(other._quests[index]))
            {
                return false;
            }
        }

        return true;
    }
}

public readonly record struct HudQuestShareInvitation(HudId ShareId, HudId QuestId, HudId SharerNameId)
{
    internal bool IsValid => !ShareId.IsEmpty && !QuestId.IsEmpty && !SharerNameId.IsEmpty;
}

public readonly record struct HudQuestReputation(HudId FactionId, long Value);

public readonly record struct HudQuestCurrency(HudId CurrencyId, long Value);

public sealed class HudQuestRewardSnapshot
{
    private readonly HudRewardItem[] _mandatoryItems;
    private readonly HudRewardItem[] _alternativeItems;
    private readonly HudQuestReputation[] _reputations;
    private readonly HudQuestCurrency[] _currencies;

    public HudQuestRewardSnapshot(
        long experience,
        long honor,
        long money,
        HudRewardItem[] mandatoryItems,
        HudRewardItem[] alternativeItems,
        HudQuestReputation[] reputations,
        HudQuestCurrency[] currencies)
    {
        if (experience < 0 || honor < 0 || money < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(experience));
        }

        ArgumentNullException.ThrowIfNull(mandatoryItems);
        ArgumentNullException.ThrowIfNull(alternativeItems);
        ArgumentNullException.ThrowIfNull(reputations);
        ArgumentNullException.ThrowIfNull(currencies);
        _mandatoryItems = (HudRewardItem[])mandatoryItems.Clone();
        _alternativeItems = (HudRewardItem[])alternativeItems.Clone();
        _reputations = (HudQuestReputation[])reputations.Clone();
        _currencies = (HudQuestCurrency[])currencies.Clone();
        if (_mandatoryItems.Any(item => !item.IsValid) || _alternativeItems.Any(item => !item.IsValid) ||
            _reputations.Any(item => item.FactionId.IsEmpty || item.Value < 0) ||
            _currencies.Any(item => item.CurrencyId.IsEmpty || item.Value < 0))
        {
            throw new ArgumentException("Quest reward entries must have stable identifiers and non-negative values.");
        }

        Experience = experience;
        Honor = honor;
        Money = money;
    }

    public long Experience { get; }

    public long Honor { get; }

    public long Money { get; }

    public ReadOnlySpan<HudRewardItem> MandatoryItems => _mandatoryItems;

    public ReadOnlySpan<HudRewardItem> AlternativeItems => _alternativeItems;

    public ReadOnlySpan<HudQuestReputation> Reputations => _reputations;

    public ReadOnlySpan<HudQuestCurrency> Currencies => _currencies;

    internal int DynamicEntryCount =>
        _mandatoryItems.Length + _alternativeItems.Length + _reputations.Length + _currencies.Length;

    internal bool ContentEquals(HudQuestRewardSnapshot other) =>
        Experience == other.Experience && Honor == other.Honor && Money == other.Money &&
        MandatoryItems.SequenceEqual(other.MandatoryItems) &&
        AlternativeItems.SequenceEqual(other.AlternativeItems) &&
        Reputations.SequenceEqual(other.Reputations) && Currencies.SequenceEqual(other.Currencies);

    public static HudQuestRewardSnapshot Empty { get; } = new(0, 0, 0, [], [], [], []);
}

public enum HudQuestInfoMode
{
    None,
    Talk,
    Offer,
    ReturnCheck,
    TurnIn,
}

public enum HudQuestRefusal
{
    None,
    InvalidState,
    Prerequisite,
    Level,
    OutOfRange,
    WrongNpc,
    BagFull,
    Unavailable,
    LogFull,
    NoSpace,
    System,
    AlreadyStarted,
    AlreadyFinished,
    OnCooldown,
    TooManyActive,
    TooManyOnCooldown,
}

public readonly record struct HudQuestTalkOption(
    HudId OptionId,
    HudId LabelId,
    HudId MarkId,
    HudQuestDocument? Quest)
{
    internal bool IsValid => !OptionId.IsEmpty && !LabelId.IsEmpty;
}

public sealed class HudQuestInfoSnapshot
{
    private readonly HudQuestTalkOption[] _talkOptions;

    public HudQuestInfoSnapshot(
        HudQuestInfoMode mode,
        HudQuestDocument? quest,
        ulong npcEntityId,
        HudQuestRewardSnapshot? reward = null,
        HudQuestRefusal refusal = HudQuestRefusal.None,
        HudQuestTalkOption[]? talkOptions = null,
        int selectedTalkOption = -1)
    {
        _talkOptions = talkOptions is null ? [] : (HudQuestTalkOption[])talkOptions.Clone();
        if (_talkOptions.Length > HudProduct.QuestTalkOptionCount ||
            _talkOptions.Any(option => !option.IsValid) ||
            selectedTalkOption < -1 || selectedTalkOption >= _talkOptions.Length)
        {
            throw new ArgumentException("NPC talk state exceeds the authored twenty-option pool.", nameof(talkOptions));
        }

        if (mode == HudQuestInfoMode.None)
        {
            if (quest is not null || npcEntityId != 0 || _talkOptions.Length != 0)
            {
                throw new ArgumentException("A closed quest-info snapshot cannot retain quest interaction state.");
            }
        }
        else if (mode == HudQuestInfoMode.Talk)
        {
            if (quest is not null || npcEntityId == 0 || _talkOptions.Length == 0)
            {
                throw new ArgumentException("Talk mode needs an NPC and at least one authored talk option.");
            }
        }
        else if (quest is null || npcEntityId == 0 ||
            (mode == HudQuestInfoMode.Offer && quest.State != HudQuestClientState.Offered) ||
            (mode == HudQuestInfoMode.ReturnCheck &&
                quest.State is not HudQuestClientState.InProgress and not HudQuestClientState.Completable) ||
            (mode == HudQuestInfoMode.TurnIn && quest.State != HudQuestClientState.Completable))
        {
            throw new ArgumentException("Quest-info mode must match an offer or completable quest and its NPC.");
        }

        Mode = mode;
        Quest = quest;
        NpcEntityId = npcEntityId;
        Reward = reward ?? HudQuestRewardSnapshot.Empty;
        Refusal = refusal;
        SelectedTalkOption = selectedTalkOption;
    }

    public HudQuestInfoMode Mode { get; }

    public HudQuestDocument? Quest { get; }

    public ulong NpcEntityId { get; }

    public HudQuestRewardSnapshot Reward { get; }

    public HudQuestRefusal Refusal { get; }

    public ReadOnlySpan<HudQuestTalkOption> TalkOptions => _talkOptions;

    public int SelectedTalkOption { get; }

    internal HudQuestInfoSnapshot WithSelectedTalkOption(int selectedTalkOption)
    {
        HudQuestDocument? selectedQuest = _talkOptions[selectedTalkOption].Quest;
        HudQuestInfoMode mode = selectedQuest?.State switch
        {
            HudQuestClientState.Offered => HudQuestInfoMode.Offer,
            HudQuestClientState.Completable => HudQuestInfoMode.TurnIn,
            HudQuestClientState.InProgress => HudQuestInfoMode.ReturnCheck,
            _ => Mode,
        };
        return new(mode, selectedQuest ?? Quest, NpcEntityId, selectedQuest?.Reward ?? Reward,
            Refusal, _talkOptions, selectedTalkOption);
    }

    internal bool ContentEquals(HudQuestInfoSnapshot other) =>
        Mode == other.Mode && NpcEntityId == other.NpcEntityId && Refusal == other.Refusal &&
        SelectedTalkOption == other.SelectedTalkOption && TalkOptions.SequenceEqual(other.TalkOptions) &&
        (ReferenceEquals(Quest, other.Quest) || (Quest is not null && other.Quest is not null && Quest.ContentEquals(other.Quest))) &&
        Reward.ContentEquals(other.Reward);

    public static HudQuestInfoSnapshot Closed { get; } = new(HudQuestInfoMode.None, null, 0);
}

public readonly record struct HudCharacterStat(
    HudId StatId,
    float? BaseValue,
    float? EffectiveValue,
    float? LongTermValue)
{
    internal bool IsValid => !StatId.IsEmpty && IsFinite(BaseValue) &&
        IsFinite(EffectiveValue) && IsFinite(LongTermValue);

    private static bool IsFinite(float? value) => !value.HasValue || float.IsFinite(value.Value);
}

/// <summary>Retail Slot01..Slot21 order. Bag is Slot20; death insurance is Slot21.</summary>
public enum HudCharacterEquipmentRole
{
    MainHand,
    OffHand,
    Ranged,
    Helm,
    Mantle,
    Cloak,
    Armor,
    Gloves,
    Belt,
    Pants,
    Boots,
    Earrings,
    Necklace,
    Tabard,
    Shirt,
    Bracers,
    Ring1,
    Ring2,
    Trinket,
    Bag,
    DeathInsurance,
}

public sealed class HudCharacterSnapshot
{
    private readonly HudItemStack?[] _equipment;
    private readonly HudCharacterStat[] _stats;

    public HudCharacterSnapshot(
        HudId nameId,
        int level,
        HudItemStack?[] equipment,
        HudCharacterStat[] stats)
    {
        if (nameId.IsEmpty || level <= 0)
        {
            throw new ArgumentException("Character snapshots need a name and positive level.");
        }

        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(stats);
        if (equipment.Length != HudProduct.CharacterEquipmentSlotCount ||
            stats.Length != HudProduct.CharacterStatCount ||
            equipment.Any(item => item is { IsValid: false }) || stats.Any(stat => !stat.IsValid))
        {
            throw new ArgumentException("Character snapshots must match the authored equipment, bag, and stat census.");
        }

        _equipment = (HudItemStack?[])equipment.Clone();
        for (int index = 0; index < _equipment.Length; index++)
        {
            if (_equipment[index] is not { } item)
            {
                continue;
            }

            for (int earlier = 0; earlier < index; earlier++)
            {
                if (_equipment[earlier]?.InstanceId == item.InstanceId)
                {
                    throw new ArgumentException("A character item instance cannot occupy multiple equipment roles.", nameof(equipment));
                }
            }
        }

        _stats = (HudCharacterStat[])stats.Clone();
        NameId = nameId;
        Level = level;
    }

    public HudId NameId { get; }

    public int Level { get; }

    public ReadOnlySpan<HudItemStack?> Equipment => _equipment;

    public HudItemStack? Bag => _equipment[HudProduct.CharacterBagSlot];

    public HudItemStack? DeathInsurance => _equipment[HudProduct.CharacterDeathInsuranceSlot];

    public ReadOnlySpan<HudCharacterStat> Stats => _stats;

    internal bool ContentEquals(HudCharacterSnapshot other) =>
        NameId == other.NameId && Level == other.Level &&
        Equipment.SequenceEqual(other.Equipment) && Stats.SequenceEqual(other.Stats);
}
