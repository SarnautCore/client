namespace SarnautCore.NativeHud;

public enum HudEventKind
{
    ActionSlotChanged,
    ActionSlotCleared,
    UnitChanged,
    UnitRemoved,
    FeedbackRaised,
    FeedbackCancelled,
    QuestTracked,
    QuestUntracked,
    ChatReceived,
    ChatRemoved,
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
    HudChatMessage? Chat)
{
    public static HudEvent ActionSlotChanged(
        HudStamp stamp,
        int slot,
        HudId abilityId,
        int cooldownMilliseconds,
        bool enabled = true) =>
        new(HudEventKind.ActionSlotChanged, stamp, HudId.Empty, 0, slot, cooldownMilliseconds, 0, enabled, abilityId, default, null, null);

    public static HudEvent ActionSlotCleared(HudStamp stamp, int slot) =>
        new(HudEventKind.ActionSlotCleared, stamp, HudId.Empty, 0, slot, 0, 0, false, HudId.Empty, default, null, null);

    public static HudEvent UnitChanged(HudStamp stamp, ulong entityId, HudId name, int health, int maximumHealth) =>
        new(HudEventKind.UnitChanged, stamp, HudId.Empty, entityId, -1, health, maximumHealth, true, name, default, null, null);

    public static HudEvent UnitRemoved(HudStamp stamp, ulong entityId) =>
        new(HudEventKind.UnitRemoved, stamp, HudId.Empty, entityId, -1, 0, 0, false, HudId.Empty, default, null, null);

    public static HudEvent FeedbackRaised(
        HudStamp stamp,
        HudId eventId,
        HudFeedbackKind kind,
        ulong entityId,
        int amount,
        bool critical = false) =>
        new(HudEventKind.FeedbackRaised, stamp, eventId, entityId, -1, amount, 0, critical, HudId.Empty, kind, null, null);

    public static HudEvent FeedbackCancelled(HudStamp stamp, HudId eventId) =>
        new(HudEventKind.FeedbackCancelled, stamp, eventId, 0, -1, 0, 0, false, HudId.Empty, default, null, null);

    public static HudEvent QuestTracked(HudStamp stamp, HudQuestSnapshot snapshot) =>
        new(HudEventKind.QuestTracked, stamp, HudId.Empty, 0, -1, 0, 0, false, snapshot.QuestId, default, snapshot, null);

    public static HudEvent QuestUntracked(HudStamp stamp, HudId questId) =>
        new(HudEventKind.QuestUntracked, stamp, HudId.Empty, 0, -1, 0, 0, false, questId, default, null, null);

    public static HudEvent ChatReceived(HudStamp stamp, HudChatMessage message)
    {
        message.Validate();
        return new HudEvent(HudEventKind.ChatReceived, stamp, message.EventId, message.SenderEntityId, -1, 0, 0,
            message.WorldBubble, message.ChannelId, default, null, message);
    }

    public static HudEvent ChatRemoved(HudStamp stamp, HudId eventId) =>
        new(HudEventKind.ChatRemoved, stamp, eventId, 0, -1, 0, 0, false, HudId.Empty, default, null, null);

    internal bool PayloadEquals(in HudEvent other) =>
        Kind == other.Kind && EventId == other.EventId && EntityId == other.EntityId && Slot == other.Slot &&
        Value == other.Value && Auxiliary == other.Auxiliary && Flag == other.Flag &&
        ContentId == other.ContentId && FeedbackKind == other.FeedbackKind &&
        (ReferenceEquals(Quest, other.Quest) || (Quest is not null && other.Quest is not null && Quest.ContentEquals(other.Quest))) &&
        Equals(Chat, other.Chat);
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
    HudFocus Focus,
    HudPoint Pointer,
    HudPoint MaskPoint,
    float MaskAlpha,
    bool HasMaskSample,
    HudId Text,
    HudPointerSource PointerSource)
{
    public static HudInput ActivateAction(int slot) => new(HudInputKind.ActivateAction, HudId.Empty, 0, slot, default, default, default, 0, false, HudId.Empty, default);

    public static HudInput SelectWorldEntity(ulong entityId) => new(HudInputKind.SelectWorldEntity, HudId.Empty, entityId, -1, default, default, default, 0, false, HudId.Empty, default);

    public static HudInput InteractWorldEntity(ulong entityId) => new(HudInputKind.InteractWorldEntity, HudId.Empty, entityId, -1, default, default, default, 0, false, HudId.Empty, default);

    public static HudInput RequestFocus(HudFocus focus) => new(HudInputKind.RequestFocus, HudId.Empty, 0, -1, focus, default, default, 0, false, HudId.Empty, default);

    public static HudInput ReleaseFocus(HudFocus focus) => new(HudInputKind.ReleaseFocus, HudId.Empty, 0, -1, focus, default, default, 0, false, HudId.Empty, default);

    public static HudInput Cancel() => new(HudInputKind.Cancel, HudId.Empty, 0, -1, default, default, default, 0, false, HudId.Empty, default);

    public static HudInput PointerMoved(HudId target, HudPoint pointer, HudPointerSource source, float maskAlpha = 0, bool hasMaskSample = false, HudPoint maskPoint = default) =>
        new(HudInputKind.PointerMoved, target, 0, -1, default, pointer, maskPoint, maskAlpha, hasMaskSample, HudId.Empty, source);

    public static HudInput PointerEvent(HudInputKind kind, HudId target, HudPoint pointer, HudPointerSource source, float maskAlpha = 0, bool hasMaskSample = false, HudPoint maskPoint = default)
    {
        if (kind is < HudInputKind.PointerMoved or > HudInputKind.DragEnded)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new HudInput(kind, target, 0, -1, default, pointer, maskPoint, maskAlpha, hasMaskSample, HudId.Empty, source);
    }

    public static HudInput SubmitChat(HudId text) => new(HudInputKind.SubmitChat, HudId.Empty, 0, -1, default, default, default, 0, false, text, default);
}

public enum HudCommandKind
{
    ActivateAction,
    SelectWorldEntity,
    SubmitChat,
    InteractWorldEntity,
}

public readonly record struct HudCommand(HudCommandKind Kind, int Slot, ulong EntityId, HudId Value)
{
    public static HudCommand ActivateAction(int slot, HudId abilityId) => new(HudCommandKind.ActivateAction, slot, 0, abilityId);

    public static HudCommand SelectWorldEntity(ulong entityId) => new(HudCommandKind.SelectWorldEntity, -1, entityId, HudId.Empty);

    public static HudCommand SubmitChat(HudId text) => new(HudCommandKind.SubmitChat, -1, 0, text);

    public static HudCommand InteractWorldEntity(ulong entityId) => new(HudCommandKind.InteractWorldEntity, -1, entityId, HudId.Empty);
}

public enum HudDispatchStatus
{
    Accepted,
    RejectedQueueFull,
    RejectedInvalid,
    Disposed,
}

public readonly record struct HudDispatchResult(HudDispatchStatus Status, bool Consumed);
