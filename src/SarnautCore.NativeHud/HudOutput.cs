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
    UnitPlate,
    Overtip,
}

[Flags]
public enum HudRefreshAreas
{
    None = 0,
    ActionSlots = 1 << 0,
    UnitPlates = 1 << 1,
    Overtips = 1 << 2,
    Feedback = 1 << 3,
    QuestTracker = 1 << 4,
    Chat = 1 << 5,
    WorldChat = 1 << 6,
    Focus = 1 << 7,
    Cursor = 1 << 8,
    VirtualPointer = 1 << 9,
    All = ActionSlots | UnitPlates | Overtips | Feedback | QuestTracker | Chat |
        WorldChat | Focus | Cursor | VirtualPointer,
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
        HudOvertipView[] overtips)
    {
        _actionSlots = actionSlots;
        _feedback = feedback;
        _quests = quests;
        _chat = chat;
        _units = units;
        _unitPlates = unitPlates;
        _overtips = overtips;
    }

    public ReadOnlySpan<HudActionSlotView> ActionSlots => _actionSlots;

    public ReadOnlySpan<HudFeedbackView> Feedback => _feedback;

    public ReadOnlySpan<HudQuestView> Quests => _quests;

    public ReadOnlySpan<HudChatView> Chat => _chat;

    public ReadOnlySpan<HudUnitView> Units => _units;

    public ReadOnlySpan<HudUnitPlateView> UnitPlates => _unitPlates;

    public ReadOnlySpan<HudOvertipView> Overtips => _overtips;

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

    private static HudRefreshAreas AreaFor(HudChangeKind kind) => kind switch
    {
        HudChangeKind.ActionSlot => HudRefreshAreas.ActionSlots,
        HudChangeKind.UnitPlate => HudRefreshAreas.UnitPlates,
        HudChangeKind.Overtip => HudRefreshAreas.Overtips,
        HudChangeKind.Feedback or HudChangeKind.Projection => HudRefreshAreas.Feedback,
        HudChangeKind.QuestTracker => HudRefreshAreas.QuestTracker,
        HudChangeKind.Chat => HudRefreshAreas.Chat,
        HudChangeKind.WorldChatProjection => HudRefreshAreas.WorldChat,
        HudChangeKind.Focus => HudRefreshAreas.Focus,
        HudChangeKind.Cursor => HudRefreshAreas.Cursor,
        HudChangeKind.VirtualPointer => HudRefreshAreas.VirtualPointer,
        _ => HudRefreshAreas.None,
    };
}
