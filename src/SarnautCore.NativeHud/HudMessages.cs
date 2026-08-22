namespace SarnautCore.NativeHud;

public enum HudEventKind
{
    ActionSlotChanged,
    ActionSlotCleared,
    TargetSelectionChanged,
    UnitChanged,
    UnitRemoved,
    FeedbackRaised,
    FeedbackCancelled,
    QuestTracked,
    QuestUntracked,
    ChatReceived,
    ChatRemoved,
    InventoryReplaced,
    InventoryCooldownStarted,
    InventoryCooldownFinished,
    LootReplaced,
    QuestLogReplaced,
    QuestInfoReplaced,
    CharacterReplaced,
    MessageBoxOffered,
    MessageBoxWithdrawn,
}

public readonly record struct HudUnitPresentation(HudPlateAssignment Plate, bool OvertipCandidate)
{
    public static HudUnitPresentation OvertipOnly => new(HudPlateAssignment.None, true);
}

public enum HudTargetSelectionRefusal
{
    Unspecified = 0,
    None = 1,
    NoTarget = 2,
    InvalidTarget = 3,
    TargetDead = 4,
}

public readonly record struct HudQuestObjective(uint Index, HudId TextId, int Current, int Required, bool ShowCount);

/// <summary>One atomic, ordered quest tracker replacement.</summary>
public sealed class HudQuestSnapshot
{
    private readonly HudQuestObjective[] _objectives;

    public HudQuestSnapshot(HudId questId, HudId titleId, bool completable, HudQuestObjective[] objectives)
    {
        if (questId.IsEmpty || titleId.IsEmpty)
        {
            throw new ArgumentException("Quest and title identifiers are required.");
        }

        ArgumentNullException.ThrowIfNull(objectives);
        _objectives = (HudQuestObjective[])objectives.Clone();
        for (int index = 0; index < _objectives.Length; index++)
        {
            HudQuestObjective objective = _objectives[index];
            if (objective.TextId.IsEmpty || objective.Current < 0 || objective.Required <= 0 || objective.Current > objective.Required)
            {
                throw new ArgumentException("Quest objectives must contain a localization key and a valid count.", nameof(objectives));
            }

            if (index > 0 && _objectives[index - 1].Index >= objective.Index)
            {
                throw new ArgumentException("Quest objectives must be strictly ordered by source index.", nameof(objectives));
            }
        }

        QuestId = questId;
        TitleId = titleId;
        Completable = completable;
    }

    public HudId QuestId { get; }

    public HudId TitleId { get; }

    public bool Completable { get; }

    public ReadOnlySpan<HudQuestObjective> Objectives => _objectives;

    internal bool ContentEquals(HudQuestSnapshot other) =>
        QuestId == other.QuestId && TitleId == other.TitleId && Completable == other.Completable &&
        Objectives.SequenceEqual(other.Objectives);
}

public sealed record HudChatMessage(
    HudId EventId,
    HudId ChannelId,
    ulong SenderEntityId,
    HudId SenderNameId,
    string Text,
    bool WorldBubble)
{
    public HudChatMessage Validate()
    {
        if (EventId.IsEmpty || ChannelId.IsEmpty || string.IsNullOrWhiteSpace(Text) ||
            (WorldBubble && SenderEntityId == 0))
        {
            throw new ArgumentException("Chat messages require event, channel, text, and a sender entity for world bubbles.");
        }

        return this;
    }
}

/// <summary>A closed authoritative or transient event accepted by the HUD runtime.</summary>
public readonly record struct HudEvent(
    HudEventKind Kind,
    HudStamp Stamp,
    HudId EventId,
    ulong EntityId,
    int Slot,
    int Value,
    int Auxiliary,
    bool Flag,
    HudId ContentId,
    HudFeedbackKind FeedbackKind,
    HudQuestSnapshot? Quest,
    HudChatMessage? Chat,
    HudUnitPresentation UnitPresentation,
    HudInventorySnapshot? Inventory,
    HudLootSnapshot? Loot,
    HudQuestLogSnapshot? QuestLog,
    HudQuestInfoSnapshot? QuestInfo,
    HudCharacterSnapshot? Character,
    HudMessageBoxRequest? MessageBox = null)
{
    public static HudEvent ActionSlotChanged(
        HudStamp stamp,
        int slot,
        HudId abilityId,
        int cooldownMilliseconds,
        bool enabled = true,
        int cooldownDurationMilliseconds = -1) =>
        new(HudEventKind.ActionSlotChanged, stamp, HudId.Empty, 0, slot, cooldownMilliseconds,
            cooldownDurationMilliseconds < 0 ? cooldownMilliseconds : cooldownDurationMilliseconds,
            enabled, abilityId, default, null, null, default, null, null, null, null, null);

    public static HudEvent ActionSlotCleared(HudStamp stamp, int slot) =>
        new(HudEventKind.ActionSlotCleared, stamp, HudId.Empty, 0, slot, 0, 0, false, HudId.Empty, default, null, null, default, null, null, null, null, null);

    /// <summary>Authoritative selected target; entity zero explicitly clears selection.</summary>
    public static HudEvent TargetSelectionChanged(
        HudStamp stamp,
        ulong entityId,
        HudTargetSelectionRefusal refusal = HudTargetSelectionRefusal.None) =>
        new(HudEventKind.TargetSelectionChanged, stamp, HudId.Empty, entityId, -1, 0, (int)refusal, true,
            HudId.Empty, default, null, null, default, null, null, null, null, null);

    public static HudEvent UnitChanged(
        HudStamp stamp,
        ulong entityId,
        HudId name,
        int health,
        int maximumHealth,
        HudUnitPresentation? presentation = null) =>
        new(HudEventKind.UnitChanged, stamp, HudId.Empty, entityId, -1, health, maximumHealth, true, name, default, null, null,
            presentation ?? HudUnitPresentation.OvertipOnly, null, null, null, null, null);

    public static HudEvent UnitRemoved(HudStamp stamp, ulong entityId) =>
        new(HudEventKind.UnitRemoved, stamp, HudId.Empty, entityId, -1, 0, 0, false, HudId.Empty, default, null, null, default, null, null, null, null, null);

    public static HudEvent FeedbackRaised(
        HudStamp stamp,
        HudId eventId,
        HudFeedbackKind kind,
        ulong entityId,
        int amount,
        bool critical = false) =>
        new(HudEventKind.FeedbackRaised, stamp, eventId, entityId, -1, amount, 0, critical, HudId.Empty, kind, null, null, default, null, null, null, null, null);

    public static HudEvent FeedbackCancelled(HudStamp stamp, HudId eventId) =>
        new(HudEventKind.FeedbackCancelled, stamp, eventId, 0, -1, 0, 0, false, HudId.Empty, default, null, null, default, null, null, null, null, null);

    public static HudEvent QuestTracked(HudStamp stamp, HudQuestSnapshot snapshot) =>
        new(HudEventKind.QuestTracked, stamp, HudId.Empty, 0, -1, 0, 0, false, snapshot.QuestId, default, snapshot, null, default, null, null, null, null, null);

    public static HudEvent QuestUntracked(HudStamp stamp, HudId questId) =>
        new(HudEventKind.QuestUntracked, stamp, HudId.Empty, 0, -1, 0, 0, false, questId, default, null, null, default, null, null, null, null, null);

    public static HudEvent ChatReceived(HudStamp stamp, HudChatMessage message)
    {
        message.Validate();
        return new HudEvent(HudEventKind.ChatReceived, stamp, message.EventId, message.SenderEntityId, -1, 0, 0,
            message.WorldBubble, message.ChannelId, default, null, message, default, null, null, null, null, null);
    }

    public static HudEvent ChatRemoved(HudStamp stamp, HudId eventId) =>
        new(HudEventKind.ChatRemoved, stamp, eventId, 0, -1, 0, 0, false, HudId.Empty, default, null, null, default, null, null, null, null, null);

    public static HudEvent InventoryReplaced(HudStamp stamp, HudInventorySnapshot snapshot) =>
        new(HudEventKind.InventoryReplaced, stamp, HudId.Empty, 0, -1, 0, 0, false, HudId.Empty, default,
            null, null, default, snapshot, null, null, null, null);

    public static HudEvent InventoryCooldownStarted(
        HudStamp stamp,
        int slot,
        HudId spellId,
        int remainingMilliseconds,
        int durationMilliseconds) =>
        new(HudEventKind.InventoryCooldownStarted, stamp, HudId.Empty, 0, slot, remainingMilliseconds,
            durationMilliseconds, true, spellId, default, null, null, default, null, null, null, null, null);

    public static HudEvent InventoryCooldownFinished(HudStamp stamp, int slot, HudId spellId) =>
        new(HudEventKind.InventoryCooldownFinished, stamp, HudId.Empty, 0, slot, 0, 0, false,
            spellId, default, null, null, default, null, null, null, null, null);

    public static HudEvent LootReplaced(HudStamp stamp, HudLootSnapshot snapshot) =>
        new(HudEventKind.LootReplaced, stamp, HudId.Empty, snapshot.CorpseEntityId, -1, 0, 0, snapshot.Open,
            HudId.Empty, default, null, null, default, null, snapshot, null, null, null);

    public static HudEvent QuestLogReplaced(HudStamp stamp, HudQuestLogSnapshot snapshot) =>
        new(HudEventKind.QuestLogReplaced, stamp, HudId.Empty, 0, -1, 0, 0, false, HudId.Empty, default,
            null, null, default, null, null, snapshot, null, null);

    public static HudEvent QuestInfoReplaced(HudStamp stamp, HudQuestInfoSnapshot snapshot) =>
        new(HudEventKind.QuestInfoReplaced, stamp, HudId.Empty, snapshot.NpcEntityId, -1, 0, 0,
            snapshot.Mode != HudQuestInfoMode.None, snapshot.Quest?.QuestId ?? HudId.Empty, default,
            null, null, default, null, null, null, snapshot, null);

    public static HudEvent CharacterReplaced(HudStamp stamp, HudCharacterSnapshot snapshot) =>
        new(HudEventKind.CharacterReplaced, stamp, HudId.Empty, 0, -1, snapshot.Level, 0, false,
            snapshot.NameId, default, null, null, default, null, null, null, null, snapshot);

    public static HudEvent MessageBoxOffered(HudStamp stamp, HudMessageBoxRequest request)
    {
        if (!request.IsValid)
        {
            throw new ArgumentException("Message-box request is invalid.", nameof(request));
        }

        return new HudEvent(HudEventKind.MessageBoxOffered, stamp, request.RequestId, 0, -1, 0, 0,
            false, request.RelatedId, default, null, null, default, null, null, null, null, null, request);
    }

    public static HudEvent MessageBoxWithdrawn(HudStamp stamp, HudId requestId) =>
        new(HudEventKind.MessageBoxWithdrawn, stamp, requestId, 0, -1, 0, 0, false, HudId.Empty,
            default, null, null, default, null, null, null, null, null);

    internal bool PayloadEquals(in HudEvent other) =>
        Kind == other.Kind && EventId == other.EventId && EntityId == other.EntityId && Slot == other.Slot &&
        Value == other.Value && Auxiliary == other.Auxiliary && Flag == other.Flag &&
        ContentId == other.ContentId && FeedbackKind == other.FeedbackKind &&
        (ReferenceEquals(Quest, other.Quest) || (Quest is not null && other.Quest is not null && Quest.ContentEquals(other.Quest))) &&
        Equals(Chat, other.Chat) && UnitPresentation == other.UnitPresentation &&
        (ReferenceEquals(Inventory, other.Inventory) || (Inventory is not null && other.Inventory is not null && Inventory.ContentEquals(other.Inventory))) &&
        (ReferenceEquals(Loot, other.Loot) || (Loot is not null && other.Loot is not null && Loot.ContentEquals(other.Loot))) &&
        (ReferenceEquals(QuestLog, other.QuestLog) || (QuestLog is not null && other.QuestLog is not null && QuestLog.ContentEquals(other.QuestLog))) &&
        (ReferenceEquals(QuestInfo, other.QuestInfo) || (QuestInfo is not null && other.QuestInfo is not null && QuestInfo.ContentEquals(other.QuestInfo))) &&
        (ReferenceEquals(Character, other.Character) || (Character is not null && other.Character is not null && Character.ContentEquals(other.Character))) &&
        MessageBox == other.MessageBox;
}

public enum HudInputKind
{
    ActivateAction,
    SelectWorldEntity,
    InteractWorldEntity,
    RequestFocus,
    ReleaseFocus,
    Cancel,
    PointerMoved,
    PointerEntered,
    PointerExited,
    PointerPrimaryPressed,
    PointerPrimaryReleased,
    PointerPrimaryDoublePressed,
    PointerSecondaryPressed,
    PointerSecondaryReleased,
    PointerSecondaryDoublePressed,
    DragStarted,
    DragEnded,
    SubmitChat,
    ToggleInventory,
    CloseInventory,
    MoveInventoryItem,
    DropInventoryItem,
    UseInventoryItem,
    DressInventoryItem,
    UndressInventoryItem,
    TakeLootItem,
    TakeLootMoney,
    TakeAllLoot,
    LootPreviousPage,
    LootNextPage,
    CloseLoot,
    ToggleQuestLog,
    CloseQuestLog,
    SelectQuest,
    SelectQuestBookmark,
    EnterQuestFolder,
    LeaveQuestFolder,
    AbandonQuest,
    ConfirmAbandonQuest,
    DeclineAbandonQuest,
    ShareQuest,
    AcceptSharedQuest,
    DeclineSharedQuest,
    ResolveMessageBox,
    SelectTalkOption,
    SelectQuestReward,
    AcceptQuest,
    TurnInQuest,
    CloseQuestInfo,
    ToggleCharacter,
    CloseCharacter,
}

public enum HudPointerSource
{
    Mouse,
    Controller,
}

/// <summary>Engine facts and semantic input. Pixel-mask samples are facts, never policy.</summary>
public readonly record struct HudInput(
    HudInputKind Kind,
    HudId Target,
    ulong EntityId,
    int Slot,
    int Auxiliary,
    HudFocus Focus,
    HudPoint Pointer,
    HudPoint MaskPoint,
    float MaskAlpha,
    bool HasMaskSample,
    HudId Text,
    HudPointerSource PointerSource,
    int Value = 0,
    bool Flag = false,
    HudId SecondaryTarget = default,
    long Amount = 0)
{
    public static HudInput ActivateAction(int slot) => new(HudInputKind.ActivateAction, HudId.Empty, 0, slot, -1, default, default, default, 0, false, HudId.Empty, default);

    public static HudInput SelectWorldEntity(ulong entityId) => new(HudInputKind.SelectWorldEntity, HudId.Empty, entityId, -1, -1, default, default, default, 0, false, HudId.Empty, default);

    public static HudInput InteractWorldEntity(ulong entityId) => new(HudInputKind.InteractWorldEntity, HudId.Empty, entityId, -1, -1, default, default, default, 0, false, HudId.Empty, default);

    public static HudInput RequestFocus(HudFocus focus) => new(HudInputKind.RequestFocus, HudId.Empty, 0, -1, -1, focus, default, default, 0, false, HudId.Empty, default);

    public static HudInput ReleaseFocus(HudFocus focus) => new(HudInputKind.ReleaseFocus, HudId.Empty, 0, -1, -1, focus, default, default, 0, false, HudId.Empty, default);

    public static HudInput Cancel() => new(HudInputKind.Cancel, HudId.Empty, 0, -1, -1, default, default, default, 0, false, HudId.Empty, default);

    public static HudInput PointerMoved(HudId target, HudPoint pointer, HudPointerSource source, float maskAlpha = 0, bool hasMaskSample = false, HudPoint maskPoint = default) =>
        new(HudInputKind.PointerMoved, target, 0, -1, -1, default, pointer, maskPoint, maskAlpha, hasMaskSample, HudId.Empty, source);

    public static HudInput PointerEvent(HudInputKind kind, HudId target, HudPoint pointer, HudPointerSource source, float maskAlpha = 0, bool hasMaskSample = false, HudPoint maskPoint = default)
    {
        if (kind is < HudInputKind.PointerMoved or > HudInputKind.DragEnded)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new HudInput(kind, target, 0, -1, -1, default, pointer, maskPoint, maskAlpha, hasMaskSample, HudId.Empty, source);
    }

    public static HudInput SubmitChat(HudId text) => new(HudInputKind.SubmitChat, HudId.Empty, 0, -1, -1, default, default, default, 0, false, text, default);

    public static HudInput ToggleInventory() => Context(HudInputKind.ToggleInventory);
    public static HudInput CloseInventory() => Context(HudInputKind.CloseInventory);
    public static HudInput MoveInventoryItem(int fromSlot, int toSlot, bool moveNoMore = false) =>
        Context(HudInputKind.MoveInventoryItem, slot: fromSlot, auxiliary: toSlot, flag: moveNoMore);
    public static HudInput DropInventoryItem(int slot, int count) =>
        Context(HudInputKind.DropInventoryItem, slot: slot, value: count);
    public static HudInput UseInventoryItem(int slot) => Context(HudInputKind.UseInventoryItem, slot: slot);
    public static HudInput DressInventoryItem(int slot) => Context(HudInputKind.DressInventoryItem, slot: slot);
    public static HudInput UndressInventoryItem(int equipmentSlot) => Context(HudInputKind.UndressInventoryItem, slot: equipmentSlot);
    public static HudInput TakeLootItem(int entry) => Context(HudInputKind.TakeLootItem, slot: entry);
    public static HudInput TakeLootMoney(long amount = -1) => Context(HudInputKind.TakeLootMoney, amount: amount);
    public static HudInput TakeAllLoot() => Context(HudInputKind.TakeAllLoot);
    public static HudInput TakeLoot() => TakeAllLoot();
    public static HudInput LootPreviousPage() => Context(HudInputKind.LootPreviousPage);
    public static HudInput LootNextPage() => Context(HudInputKind.LootNextPage);
    public static HudInput CloseLoot() => Context(HudInputKind.CloseLoot);
    public static HudInput ToggleQuestLog() => Context(HudInputKind.ToggleQuestLog);
    public static HudInput CloseQuestLog() => Context(HudInputKind.CloseQuestLog);
    public static HudInput SelectQuest(HudId questId) => Context(HudInputKind.SelectQuest, target: questId);
    public static HudInput SelectQuestBookmark(HudQuestLogBookmark bookmark) =>
        Context(HudInputKind.SelectQuestBookmark, value: (int)bookmark);
    public static HudInput EnterQuestFolder(HudId folderId) => Context(HudInputKind.EnterQuestFolder, target: folderId);
    public static HudInput LeaveQuestFolder() => Context(HudInputKind.LeaveQuestFolder);
    public static HudInput AbandonQuest(HudId questId) => Context(HudInputKind.AbandonQuest, target: questId);
    public static HudInput ConfirmAbandonQuest(HudId questId) => Context(HudInputKind.ConfirmAbandonQuest, target: questId);
    public static HudInput DeclineAbandonQuest() => Context(HudInputKind.DeclineAbandonQuest);
    public static HudInput ShareQuest(HudId questId) => Context(HudInputKind.ShareQuest, target: questId);
    public static HudInput AcceptSharedQuest(HudId shareId, HudId questId) =>
        Context(HudInputKind.AcceptSharedQuest, target: shareId, secondaryTarget: questId);
    public static HudInput DeclineSharedQuest(HudId shareId, HudId questId) =>
        Context(HudInputKind.DeclineSharedQuest, target: shareId, secondaryTarget: questId);
    public static HudInput ResolveMessageBox(HudId requestId, HudMessageBoxDecision decision) =>
        Context(HudInputKind.ResolveMessageBox, target: requestId, value: (int)decision);
    public static HudInput SelectTalkOption(int option) => Context(HudInputKind.SelectTalkOption, slot: option);
    public static HudInput SelectQuestReward(int rewardIndex) => Context(HudInputKind.SelectQuestReward, slot: rewardIndex);
    public static HudInput AcceptQuest() => Context(HudInputKind.AcceptQuest);
    public static HudInput TurnInQuest() => Context(HudInputKind.TurnInQuest);
    public static HudInput CloseQuestInfo() => Context(HudInputKind.CloseQuestInfo);
    public static HudInput ToggleCharacter() => Context(HudInputKind.ToggleCharacter);
    public static HudInput CloseCharacter() => Context(HudInputKind.CloseCharacter);

    private static HudInput Context(
        HudInputKind kind,
        HudId target = default,
        int slot = -1,
        int auxiliary = -1,
        int value = 0,
        bool flag = false,
        HudId secondaryTarget = default,
        long amount = 0) =>
        new(kind, target, 0, slot, auxiliary, default, default, default, 0, false, HudId.Empty, default,
            value, flag, secondaryTarget, amount);
}

public enum HudCommandKind
{
    ActivateAction,
    SelectWorldEntity,
    SubmitChat,
    InteractWorldEntity,
    MoveInventoryItem,
    DropInventoryItem,
    UseInventoryItem,
    DressInventoryItem,
    UndressInventoryItem,
    TakeLootItem,
    TakeLootMoney,
    TakeAllLoot,
    CloseLoot,
    AbandonQuest,
    ShareQuest,
    AcceptSharedQuest,
    DeclineSharedQuest,
    AcceptQuest,
    TurnInQuest,
    ResolveMessageBox,
}

public readonly record struct HudCommand(
    HudCommandKind Kind,
    int Slot,
    int Auxiliary,
    ulong EntityId,
    HudId Value,
    int Count = 0,
    bool Flag = false,
    HudId SecondaryValue = default,
    HudStamp ExpectedRevision = default,
    long Amount = 0,
    HudId RelatedValue = default)
{
    public static HudCommand ActivateAction(int slot, HudStamp expectedRevision) =>
        new(HudCommandKind.ActivateAction, slot, -1, 0, HudId.Empty, ExpectedRevision: expectedRevision);

    public static HudCommand SelectWorldEntity(ulong entityId) => new(HudCommandKind.SelectWorldEntity, -1, -1, entityId, HudId.Empty);

    public static HudCommand SubmitChat(HudId text) => new(HudCommandKind.SubmitChat, -1, -1, 0, text);

    public static HudCommand InteractWorldEntity(ulong entityId) => new(HudCommandKind.InteractWorldEntity, -1, -1, entityId, HudId.Empty);

    public static HudCommand MoveInventoryItem(int fromSlot, int toSlot, bool moveNoMore, HudStamp expectedRevision) =>
        new(HudCommandKind.MoveInventoryItem, fromSlot, toSlot, 0, HudId.Empty, 0, moveNoMore,
            ExpectedRevision: expectedRevision);

    public static HudCommand DropInventoryItem(int slot, int count, HudStamp expectedRevision) =>
        new(HudCommandKind.DropInventoryItem, slot, -1, 0, HudId.Empty, count, ExpectedRevision: expectedRevision);

    public static HudCommand UseInventoryItem(int slot, HudStamp expectedRevision) =>
        new(HudCommandKind.UseInventoryItem, slot, -1, 0, HudId.Empty, ExpectedRevision: expectedRevision);

    public static HudCommand DressInventoryItem(int slot, HudStamp expectedRevision) =>
        new(HudCommandKind.DressInventoryItem, slot, -1, 0, HudId.Empty, ExpectedRevision: expectedRevision);

    public static HudCommand UndressInventoryItem(int slot, HudStamp expectedRevision) =>
        new(HudCommandKind.UndressInventoryItem, slot, -1, 0, HudId.Empty, ExpectedRevision: expectedRevision);

    public static HudCommand TakeLootItem(ulong corpseEntityId, int entry, HudStamp expectedRevision) =>
        new(HudCommandKind.TakeLootItem, entry, -1, corpseEntityId, HudId.Empty,
            ExpectedRevision: expectedRevision);

    public static HudCommand TakeLootMoney(ulong corpseEntityId, long amount, HudStamp expectedRevision) =>
        new(HudCommandKind.TakeLootMoney, -1, -1, corpseEntityId, HudId.Empty,
            ExpectedRevision: expectedRevision, Amount: amount);

    public static HudCommand TakeAllLoot(ulong corpseEntityId, HudStamp expectedRevision) =>
        new(HudCommandKind.TakeAllLoot, -1, -1, corpseEntityId, HudId.Empty,
            ExpectedRevision: expectedRevision);

    public static HudCommand CloseLoot(ulong corpseEntityId, HudStamp expectedRevision) =>
        new(HudCommandKind.CloseLoot, -1, -1, corpseEntityId, HudId.Empty,
            ExpectedRevision: expectedRevision);

    public static HudCommand AbandonQuest(HudId questId, HudStamp expectedRevision) =>
        new(HudCommandKind.AbandonQuest, -1, -1, 0, questId, ExpectedRevision: expectedRevision);

    public static HudCommand ShareQuest(HudId questId, HudStamp expectedRevision) =>
        new(HudCommandKind.ShareQuest, -1, -1, 0, questId, ExpectedRevision: expectedRevision);

    public static HudCommand AcceptSharedQuest(HudId shareId, HudId questId, HudStamp expectedRevision) =>
        new(HudCommandKind.AcceptSharedQuest, -1, -1, 0, shareId,
            SecondaryValue: questId, ExpectedRevision: expectedRevision);

    public static HudCommand DeclineSharedQuest(HudId shareId, HudId questId, HudStamp expectedRevision) =>
        new(HudCommandKind.DeclineSharedQuest, -1, -1, 0, shareId,
            SecondaryValue: questId, ExpectedRevision: expectedRevision);

    public static HudCommand AcceptQuest(HudId questId, ulong npcEntityId, HudStamp expectedRevision) =>
        new(HudCommandKind.AcceptQuest, -1, -1, npcEntityId, questId, ExpectedRevision: expectedRevision);

    public static HudCommand TurnInQuest(HudId questId, ulong npcEntityId, int rewardIndex, HudStamp expectedRevision) =>
        new(HudCommandKind.TurnInQuest, rewardIndex, -1, npcEntityId, questId,
            ExpectedRevision: expectedRevision);

    public static HudCommand ResolveMessageBox(
        HudId requestId,
        HudMessageBoxPurpose purpose,
        HudMessageBoxDecision decision,
        HudId relatedId,
        HudId secondaryId,
        HudStamp expectedRevision) =>
        new(HudCommandKind.ResolveMessageBox, (int)decision, (int)purpose, 0, requestId,
            SecondaryValue: secondaryId, ExpectedRevision: expectedRevision, RelatedValue: relatedId);
}

public enum HudDispatchStatus
{
    Accepted,
    RejectedQueueFull,
    RejectedInvalid,
    Disposed,
}

public readonly record struct HudDispatchResult(HudDispatchStatus Status, bool Consumed);
