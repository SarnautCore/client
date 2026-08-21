namespace SarnautCore.NativeHud;

public enum HudChangeKind
{
    ActionSlot,
    TargetSelection,
    Feedback,
    Projection,
    Focus,
    Cursor,
    VirtualPointer,
    QuestTracker,
    Chat,
    WorldChatProjection,
    UnitPlate,
    Overtip,
    Inventory,
    Loot,
    QuestLog,
    QuestInfo,
    Character,
}

[Flags]
public enum HudRefreshAreas
{
    None = 0,
    ActionSlots = 1 << 0,
    TargetSelection = 1 << 15,
    UnitPlates = 1 << 1,
    Overtips = 1 << 2,
    Feedback = 1 << 3,
    QuestTracker = 1 << 4,
    Chat = 1 << 5,
    WorldChat = 1 << 6,
    Focus = 1 << 7,
    Cursor = 1 << 8,
    VirtualPointer = 1 << 9,
    Inventory = 1 << 10,
    Loot = 1 << 11,
    QuestLog = 1 << 12,
    QuestInfo = 1 << 13,
    Character = 1 << 14,
    All = ActionSlots | TargetSelection | UnitPlates | Overtips | Feedback | QuestTracker | Chat |
        WorldChat | Focus | Cursor | VirtualPointer | Inventory | Loot | QuestLog |
        QuestInfo | Character,
}

[Flags]
public enum HudUnitChangeAreas
{
    None = 0,
    Identity = 1 << 0,
    Vitality = 1 << 1,
    Assignment = 1 << 2,
    Visibility = 1 << 3,
    Projection = 1 << 4,
    Removal = 1 << 5,
    All = Identity | Vitality | Assignment | Visibility | Projection | Removal,
}

/// <summary>A plain, role-addressed mutation for an engine adapter.</summary>
public readonly record struct HudChange(
    HudChangeKind Kind,
    HudId Element,
    int Generation,
    bool Visible,
    int Value,
    bool Flag,
    HudId ContentId,
    HudPoint Position,
    int SecondaryValue = 0,
    HudStamp Revision = default,
    HudUnitChangeAreas UnitAreas = HudUnitChangeAreas.None);

public enum HudErrorCode
{
    InvalidEvent,
    StaleAuthority,
    AuthorityConflict,
    EntityCapacityExceeded,
    UnitPlateAssignmentConflict,
    OvertipCapacityExceeded,
    QuestCapacityExceeded,
    InputQueueOverflow,
    SessionEventOverflow,
    SessionClosed,
    SessionFaulted,
    UnsupportedCommand,
    CommandQueueFull,
    DiffOverflow,
    ClockRegressed,
    InventoryCapacityExceeded,
    LootCapacityExceeded,
    QuestLogCapacityExceeded,
    QuestInfoCapacityExceeded,
}

public readonly record struct HudError(HudErrorCode Code, HudStamp Stamp, HudId RelatedId, ulong EntityId, int Index);

public readonly record struct HudActionSlotView(
    HudId Element,
    HudId AbilityId,
    int CooldownMilliseconds,
    int CooldownDurationMilliseconds,
    bool Enabled,
    HudStamp Stamp,
    bool HasAuthority);

public readonly record struct HudSelectedTargetView(
    ulong EntityId,
    bool HasAuthority,
    HudTargetSelectionRefusal Refusal,
    HudStamp Revision);

public readonly record struct HudFeedbackView(
    HudId Element,
    HudId EventId,
    HudFeedbackKind Kind,
    ulong EntityId,
    int Amount,
    bool Critical,
    int Generation,
    bool Active,
    bool Projected,
    HudPoint Position);

public readonly record struct HudQuestView(
    HudId Element,
    HudId QuestId,
    HudId TitleId,
    bool Completable,
    bool Tracked,
    HudStamp Stamp,
    HudQuestSnapshot? Snapshot);

public readonly record struct HudChatView(
    HudId EventId,
    HudId ChannelId,
    ulong SenderEntityId,
    HudId SenderNameId,
    string? Text,
    bool WorldBubble,
    bool Active,
    bool Projected,
    HudPoint Position,
    HudStamp Stamp);

public readonly record struct HudUnitView(
    ulong EntityId,
    HudId NameId,
    int Health,
    int MaximumHealth,
    HudStamp Revision,
    bool Active,
    HudPlateAssignment PlateAssignment,
    HudId PlateElement,
    bool PlateVisible,
    bool OvertipCandidate,
    HudId OvertipElement,
    bool OvertipVisible,
    HudPoint OvertipPosition);

public readonly record struct HudUnitPlateView(
    HudId Element,
    HudPlateAssignment Assignment,
    ulong EntityId,
    HudId NameId,
    int Health,
    int MaximumHealth,
    HudStamp Revision,
    bool Occupied,
    bool Visible);

public readonly record struct HudOvertipView(
    HudId Element,
    int Lane,
    ulong EntityId,
    HudId NameId,
    int Health,
    int MaximumHealth,
    HudStamp Revision,
    bool Occupied,
    bool Visible,
    HudPoint Position);

public readonly record struct HudInventorySlotView(
    HudId Element,
    int Slot,
    HudId ItemId,
    ulong InstanceId,
    int Count,
    int CounterValue,
    bool Bound,
    bool Cursed,
    bool IsQuestOperator,
    long RemoveTime,
    HudId RuneId,
    HudId RuneSlotId,
    bool Occupied,
    bool Visible);

public readonly record struct HudInventoryPartitionView(
    HudId Element,
    int Partition,
    int FirstSlot,
    int SlotCount,
    bool Visible);

public sealed class HudInventoryReadModel
{
    private readonly HudInventorySlotView[] _slots;
    private readonly HudInventoryPartitionView[] _partitions;

    internal HudInventoryReadModel(HudInventorySlotView[] slots, HudInventoryPartitionView[] partitions)
    {
        _slots = slots;
        _partitions = partitions;
    }

    public ReadOnlySpan<HudInventorySlotView> Slots => _slots;
    public ReadOnlySpan<HudInventoryPartitionView> Partitions => _partitions;
    public bool HasAuthority { get; internal set; }
    public bool Open { get; internal set; }
    public int Capacity { get; internal set; }
    public long Currency { get; internal set; }
    public HudItemReference EquippedBag { get; internal set; }
    public HudId LayoutElement { get; internal set; }
    public HudStamp Revision { get; internal set; }
}

public readonly record struct HudLootSlotView(
    HudId Element,
    int PageSlot,
    int Entry,
    HudId ItemId,
    int Count,
    bool Cursed,
    bool Occupied);

public sealed class HudLootReadModel
{
    private readonly HudLootSlotView[] _pageSlots;

    internal HudLootReadModel(HudLootSlotView[] pageSlots) => _pageSlots = pageSlots;

    public ReadOnlySpan<HudLootSlotView> PageSlots => _pageSlots;
    public bool HasAuthority { get; internal set; }
    public bool Open { get; internal set; }
    public ulong CorpseEntityId { get; internal set; }
    public long Money { get; internal set; }
    public HudLootRefusal Refusal { get; internal set; }
    public int Page { get; internal set; }
    public int PageCount { get; internal set; }
    public int EntryCount { get; internal set; }
    public HudStamp Revision { get; internal set; }
}

public readonly record struct HudQuestLogEntryView(
    HudId Element,
    int Entry,
    HudId QuestId,
    HudId TitleId,
    HudId DescriptionId,
    HudQuestClientState State,
    bool CanAbandon,
    bool Occupied,
    bool Selected,
    HudQuestDocument? Document);

public sealed class HudQuestLogReadModel
{
    private readonly HudQuestLogEntryView[] _entries;

    internal HudQuestLogReadModel(HudQuestLogEntryView[] entries) => _entries = entries;

    public ReadOnlySpan<HudQuestLogEntryView> Entries => _entries;
    public bool HasAuthority { get; internal set; }
    public bool Open { get; internal set; }
    public int Count { get; internal set; }
    public HudQuestLogBookmark ActiveBookmark { get; internal set; }
    public int SecretComponentCount { get; internal set; }
    public HudId SelectedQuestId { get; internal set; }
    public HudId PendingAbandonQuestId { get; internal set; }
    public long AbandonConfirmationExpiresAtMilliseconds { get; internal set; }
    public HudQuestShareInvitation? ShareInvitation { get; internal set; }
    public long ShareInvitationExpiresAtMilliseconds { get; internal set; }
    public HudStamp Revision { get; internal set; }
}

public readonly record struct HudQuestInfoView(
    HudId Element,
    HudId DetailElement,
    bool HasAuthority,
    bool Open,
    HudQuestInfoMode Mode,
    HudId QuestId,
    ulong NpcEntityId,
    HudQuestRefusal Refusal,
    HudQuestDocument? Quest,
    HudQuestRewardSnapshot? Reward,
    int SelectedTalkOption,
    int SelectedRewardIndex,
    HudStamp Revision);

public readonly record struct HudCharacterEquipmentView(
    HudId Element,
    int Slot,
    HudCharacterEquipmentRole Role,
    HudId ItemId,
    ulong InstanceId,
    int Count,
    bool Bound,
    bool Cursed,
    bool Occupied);

public readonly record struct HudCharacterStatView(
    HudId Element,
    int Row,
    HudId StatId,
    float? BaseValue,
    float? EffectiveValue,
    float? LongTermValue,
    bool HasAuthority);

public sealed class HudCharacterReadModel
{
    private readonly HudCharacterEquipmentView[] _equipment;
    private readonly HudCharacterStatView[] _stats;

    internal HudCharacterReadModel(HudCharacterEquipmentView[] equipment, HudCharacterStatView[] stats)
    {
        _equipment = equipment;
        _stats = stats;
    }

    public ReadOnlySpan<HudCharacterEquipmentView> Equipment => _equipment;
    public ReadOnlySpan<HudCharacterStatView> Stats => _stats;
    public bool HasAuthority { get; internal set; }
    public bool Open { get; internal set; }
    public HudId NameId { get; internal set; }
    public int Level { get; internal set; }
    public HudCharacterEquipmentView Bag { get; internal set; }
    public HudStamp Revision { get; internal set; }
}

/// <summary>Stable read model. Its arrays belong to the runtime and must not be mutated.</summary>
public sealed class HudReadModel
{
    private readonly HudActionSlotView[] _actionSlots;
    private readonly HudFeedbackView[] _feedback;
    private readonly HudQuestView[] _quests;
    private readonly HudChatView[] _chat;
    private readonly HudUnitView[] _units;
    private readonly HudUnitPlateView[] _unitPlates;
    private readonly HudOvertipView[] _overtips;

    internal HudReadModel(
        HudActionSlotView[] actionSlots,
        HudFeedbackView[] feedback,
        HudQuestView[] quests,
        HudChatView[] chat,
        HudUnitView[] units,
        HudUnitPlateView[] unitPlates,
        HudOvertipView[] overtips,
        HudInventoryReadModel inventory,
        HudLootReadModel loot,
        HudQuestLogReadModel questLog,
        HudQuestInfoView questInfo,
        HudCharacterReadModel character)
    {
        _actionSlots = actionSlots;
        _feedback = feedback;
        _quests = quests;
        _chat = chat;
        _units = units;
        _unitPlates = unitPlates;
        _overtips = overtips;
        Inventory = inventory;
        Loot = loot;
        QuestLog = questLog;
        QuestInfo = questInfo;
        Character = character;
    }

    public ReadOnlySpan<HudActionSlotView> ActionSlots => _actionSlots;

    public ReadOnlySpan<HudFeedbackView> Feedback => _feedback;

    public ReadOnlySpan<HudQuestView> Quests => _quests;

    public ReadOnlySpan<HudChatView> Chat => _chat;

    public ReadOnlySpan<HudUnitView> Units => _units;

    public ReadOnlySpan<HudUnitPlateView> UnitPlates => _unitPlates;

    public ReadOnlySpan<HudOvertipView> Overtips => _overtips;

    public HudInventoryReadModel Inventory { get; }

    public HudLootReadModel Loot { get; }

    public HudQuestLogReadModel QuestLog { get; }

    public HudQuestInfoView QuestInfo { get; internal set; }

    public HudCharacterReadModel Character { get; }

    public HudSelectedTargetView SelectedTarget { get; internal set; }

    public HudFocus Focus { get; internal set; }

    public HudId CursorId { get; internal set; }

    public HudPointerSource PointerSource { get; internal set; }

    public HudPoint Pointer { get; internal set; }

    public long FrameRevision { get; internal set; }

    public HudViewport Viewport { get; internal set; }
}

/// <summary>
/// Reused frame result. Consume its spans before calling <see cref="NativeHud.Advance"/> again.
/// </summary>
public sealed class HudDiff
{
    private readonly HudChange[] _changes;
    private readonly HudError[] _errors;
    private int _changeCount;
    private int _errorCount;

    internal HudDiff(int changeCapacity, int errorCapacity, HudReadModel readModel)
    {
        _changes = new HudChange[changeCapacity];
        _errors = new HudError[errorCapacity];
        ReadModel = readModel;
    }

    public ReadOnlySpan<HudChange> Changes => _changes.AsSpan(0, _changeCount);

    public ReadOnlySpan<HudError> Errors => _errors.AsSpan(0, _errorCount);

    public HudReadModel ReadModel { get; }

    public bool RequiresFullRefresh { get; internal set; }

    public HudRefreshAreas ChangedAreas { get; internal set; }

    public HudRefreshAreas RequiredRefreshAreas { get; internal set; }

    public long FrameRevision { get; internal set; }

    internal void Reset()
    {
        _changeCount = 0;
        _errorCount = 0;
        RequiresFullRefresh = false;
        ChangedAreas = HudRefreshAreas.None;
        RequiredRefreshAreas = HudRefreshAreas.None;
    }

    internal void AddChange(in HudChange change)
    {
        if (_changeCount == _changes.Length)
        {
            RequiresFullRefresh = true;
            RequiredRefreshAreas = HudRefreshAreas.All;
            AddError(new HudError(HudErrorCode.DiffOverflow, default, change.Element, 0, -1));
            return;
        }

        _changes[_changeCount++] = change;
        ChangedAreas |= AreaFor(change.Kind);
    }

    internal void AddError(in HudError error)
    {
        if (_errorCount < _errors.Length)
        {
            _errors[_errorCount++] = error;
        }
    }

    internal void RequireFullRefresh(HudRefreshAreas areas)
    {
        if (areas == HudRefreshAreas.None)
        {
            return;
        }

        RequiresFullRefresh = true;
        RequiredRefreshAreas |= areas;
    }

    private static HudRefreshAreas AreaFor(HudChangeKind kind) => kind switch
    {
        HudChangeKind.ActionSlot => HudRefreshAreas.ActionSlots,
        HudChangeKind.TargetSelection => HudRefreshAreas.TargetSelection,
        HudChangeKind.UnitPlate => HudRefreshAreas.UnitPlates,
        HudChangeKind.Overtip => HudRefreshAreas.Overtips,
        HudChangeKind.Feedback or HudChangeKind.Projection => HudRefreshAreas.Feedback,
        HudChangeKind.QuestTracker => HudRefreshAreas.QuestTracker,
        HudChangeKind.Chat => HudRefreshAreas.Chat,
        HudChangeKind.WorldChatProjection => HudRefreshAreas.WorldChat,
        HudChangeKind.Focus => HudRefreshAreas.Focus,
        HudChangeKind.Cursor => HudRefreshAreas.Cursor,
        HudChangeKind.VirtualPointer => HudRefreshAreas.VirtualPointer,
        HudChangeKind.Inventory => HudRefreshAreas.Inventory,
        HudChangeKind.Loot => HudRefreshAreas.Loot,
        HudChangeKind.QuestLog => HudRefreshAreas.QuestLog,
        HudChangeKind.QuestInfo => HudRefreshAreas.QuestInfo,
        HudChangeKind.Character => HudRefreshAreas.Character,
        _ => HudRefreshAreas.None,
    };
}
