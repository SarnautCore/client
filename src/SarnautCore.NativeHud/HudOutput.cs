namespace SarnautCore.NativeHud;

public enum HudChangeKind
{
    ActionSlot,
    Feedback,
    Projection,
    Focus,
    Cursor,
    VirtualPointer,
    QuestTracker,
    Chat,
    WorldChatProjection,
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
    HudPoint Position);

public enum HudErrorCode
{
    InvalidEvent,
    StaleAuthority,
    AuthorityConflict,
    EntityCapacityExceeded,
    QuestCapacityExceeded,
    InputQueueOverflow,
    SessionEventOverflow,
    SessionClosed,
    SessionFaulted,
    UnsupportedCommand,
    CommandQueueFull,
    DiffOverflow,
    ClockRegressed,
}

public readonly record struct HudError(HudErrorCode Code, HudStamp Stamp, HudId RelatedId, ulong EntityId, int Index);

public readonly record struct HudActionSlotView(
    HudId Element,
    HudId AbilityId,
    int CooldownMilliseconds,
    bool Enabled,
    HudStamp Stamp,
    bool HasAuthority);

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

/// <summary>Stable read model. Its arrays belong to the runtime and must not be mutated.</summary>
public sealed class HudReadModel
{
    private readonly HudActionSlotView[] _actionSlots;
    private readonly HudFeedbackView[] _feedback;
    private readonly HudQuestView[] _quests;
    private readonly HudChatView[] _chat;

    internal HudReadModel(
        HudActionSlotView[] actionSlots,
        HudFeedbackView[] feedback,
        HudQuestView[] quests,
        HudChatView[] chat)
    {
        _actionSlots = actionSlots;
        _feedback = feedback;
        _quests = quests;
        _chat = chat;
    }

    public ReadOnlySpan<HudActionSlotView> ActionSlots => _actionSlots;

    public ReadOnlySpan<HudFeedbackView> Feedback => _feedback;

    public ReadOnlySpan<HudQuestView> Quests => _quests;

    public ReadOnlySpan<HudChatView> Chat => _chat;

    public HudFocus Focus { get; internal set; }

    public HudId CursorId { get; internal set; }

    public HudPointerSource PointerSource { get; internal set; }

    public HudPoint Pointer { get; internal set; }
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

    internal void Reset()
    {
        _changeCount = 0;
        _errorCount = 0;
        RequiresFullRefresh = false;
    }

    internal void AddChange(in HudChange change)
    {
        if (_changeCount == _changes.Length)
        {
            RequiresFullRefresh = true;
            AddError(new HudError(HudErrorCode.DiffOverflow, default, change.Element, 0, -1));
            return;
        }

        _changes[_changeCount++] = change;
    }

    internal void AddError(in HudError error)
    {
        if (_errorCount < _errors.Length)
        {
            _errors[_errorCount++] = error;
        }
    }
}
