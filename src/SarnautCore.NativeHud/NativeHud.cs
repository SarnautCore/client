namespace SarnautCore.NativeHud;

/// <summary>
/// Engine-neutral HUD state machine. The runtime owns ordering, stable pools, timelines,
/// projection policy, focus, cursor selection, and bounded delivery.
/// </summary>
public sealed class NativeHud : IDisposable
{
    private readonly HudProduct _product;
    private readonly IHudSession _session;
    private readonly IHudWorld _world;
    private readonly ActionState[] _actions;
    private readonly FeedbackState[] _feedback;
    private readonly EntityState[] _entities;
    private readonly UnitPlateState[] _unitPlates;
    private readonly OvertipState[] _overtips;
    private readonly QuestState[] _quests;
    private readonly QuestTombstone[] _questTombstones;
    private readonly ChatState[] _chat;
    private readonly TransientState[] _transients;
    private readonly HudEvent[] _eventBuffer;
    private readonly HudInput[] _inputQueue;
    private readonly HudActionSlotView[] _actionViews;
    private readonly HudFeedbackView[] _feedbackViews;
    private readonly HudQuestView[] _questViews;
    private readonly HudChatView[] _chatViews;
    private readonly HudUnitView[] _unitViews;
    private readonly HudUnitPlateView[] _unitPlateViews;
    private readonly HudOvertipView[] _overtipViews;
    private readonly HudDiff _diff;
    private int _inputHead;
    private int _inputCount;
    private int _transientCursor;
    private int _chatCursor;
    private int _questTombstoneCursor;
    private int _pendingInputOverflows;
    private long _lastNow;
    private long _frameRevision;
    private HudFocus _focus;
    private HudFocus _focusBeforeDrag;
    private HudId _hoverElement;
    private HudCursor _cursor;
    private HudPointerSource _pointerSource;
    private HudPoint _pointer;
    private HudSessionState _lastSessionState;
    private bool _firstFrame = true;
    private bool _disposed;

    private NativeHud(HudProduct product, IHudSession session, IHudWorld world)
    {
        _product = product;
        _session = session;
        _world = world;
        _actions = new ActionState[HudProduct.ActionSlotCount];
        _feedback = new FeedbackState[3 * HudProduct.FeedbackPoolCount];
        _entities = new EntityState[product.MaxEntities];
        _unitPlates = new UnitPlateState[product.UnitPlates.Length];
        _overtips = new OvertipState[product.MaxOvertips];
        _quests = new QuestState[HudProduct.QuestTrackerRowCount];
        _questTombstones = new QuestTombstone[64];
        _chat = new ChatState[product.MaxChatEntries];
        _transients = new TransientState[Math.Max(64, product.MaxSessionEventsPerFrame * 2)];
        _eventBuffer = new HudEvent[product.MaxSessionEventsPerFrame];
        _inputQueue = new HudInput[product.MaxPendingInputs];
        _actionViews = new HudActionSlotView[_actions.Length];
        _feedbackViews = new HudFeedbackView[_feedback.Length];
        _questViews = new HudQuestView[_quests.Length];
        _chatViews = new HudChatView[_chat.Length];
        _unitViews = new HudUnitView[_entities.Length];
        _unitPlateViews = new HudUnitPlateView[_unitPlates.Length];
        _overtipViews = new HudOvertipView[_overtips.Length];

        for (int index = 0; index < _entities.Length; index++)
        {
            _entities[index].PlateIndex = -1;
            _entities[index].OvertipIndex = -1;
            UpdateUnitView(index);
        }

        for (int index = 0; index < _unitPlates.Length; index++)
        {
            _unitPlates[index].Assignment = product.UnitPlates[index].Assignment;
            _unitPlates[index].Element = product.UnitPlates[index].Element;
            UpdateUnitPlateView(index);
        }

        for (int index = 0; index < _overtips.Length; index++)
        {
            _overtips[index].Element = product.OvertipPrototype;
            UpdateOvertipView(index);
        }

        for (int index = 0; index < _quests.Length; index++)
        {
            _quests[index].Element = product.QuestTrackerRows[index];
            UpdateQuestView(index);
        }

        for (int index = 0; index < _actions.Length; index++)
        {
            _actions[index].Element = product.ActionSlots[index];
            UpdateActionView(index);
        }

        int feedbackIndex = 0;
        for (int kind = 0; kind < 3; kind++)
        {
            HudFeedbackKind feedbackKind = (HudFeedbackKind)kind;
            HudId[] elements = product.GetFeedbackElements(feedbackKind);
            for (int lane = 0; lane < elements.Length; lane++)
            {
                _feedback[feedbackIndex].Element = elements[lane];
                _feedback[feedbackIndex].Kind = feedbackKind;
                UpdateFeedbackView(feedbackIndex);
                feedbackIndex++;
            }
        }

        var readModel = new HudReadModel(
            _actionViews,
            _feedbackViews,
            _questViews,
            _chatViews,
            _unitViews,
            _unitPlateViews,
            _overtipViews)
        {
            Focus = HudFocus.World,
            CursorId = product.Cursors.Resolve(HudCursor.Default),
        };
        _diff = new HudDiff(product.MaxChangesPerFrame, product.MaxErrorsPerFrame, readModel);
        _lastSessionState = HudSessionState.Open;
    }

    public static NativeHud Open(HudProduct product, IHudSession session, IHudWorld world)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(world);
        return new NativeHud(product, session, world);
    }

    /// <summary>Queues typed input. The bounded queue is drained by the next frame.</summary>
    public HudDispatchResult Dispatch(in HudInput input)
    {
        if (_disposed)
        {
            return new HudDispatchResult(HudDispatchStatus.Disposed, false);
        }

        if (!IsValidInput(input))
        {
            return new HudDispatchResult(HudDispatchStatus.RejectedInvalid, false);
        }

        bool consumed = IsPointerButton(input.Kind) && AcceptPointerTarget(input);

        if (_inputCount == _inputQueue.Length)
        {
            _pendingInputOverflows++;
            return new HudDispatchResult(HudDispatchStatus.RejectedQueueFull, consumed);
        }

        int tail = (_inputHead + _inputCount) % _inputQueue.Length;
        _inputQueue[tail] = input;
        _inputCount++;
        return new HudDispatchResult(HudDispatchStatus.Accepted, consumed);
    }

    /// <summary>
    /// Advances one deterministic frame. The returned object and its spans are reused by the
    /// next call, so an adapter must consume them before advancing again.
    /// </summary>
    public HudDiff Advance(in HudFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _diff.Reset();

        long now = frame.NowMilliseconds;
        if (now < _lastNow)
        {
            AddError(HudErrorCode.ClockRegressed, default, HudId.Empty, 0, -1);
            now = _lastNow;
        }

        _lastNow = now;
        if (_firstFrame)
        {
            EmitFullState();
            _firstFrame = false;
        }

        while (_pendingInputOverflows > 0)
        {
            AddError(HudErrorCode.InputQueueOverflow, default, HudId.Empty, 0, _pendingInputOverflows);
            _pendingInputOverflows = 0;
        }

        ReadSessionEvents();
        DrainInput();
        AdvanceFeedback(now, frame.Viewport);
        AdvanceWorldChat(frame.Viewport);
        AdvanceOvertips(frame.Viewport);
        UpdateCursor();
        _frameRevision++;
        _diff.FrameRevision = _frameRevision;
        _diff.ReadModel.FrameRevision = _frameRevision;
        _diff.ReadModel.Viewport = frame.Viewport;
        return _diff;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inputCount = 0;
    }

    private void ReadSessionEvents()
    {
        HudSessionRead read = _session.Read(_eventBuffer);
        int count = read.Count;
        if ((uint)count > (uint)_eventBuffer.Length || read.DroppedCount < 0)
        {
            AddError(HudErrorCode.InvalidEvent, default, HudId.Empty, 0, -1);
            count = Math.Clamp(count, 0, _eventBuffer.Length);
        }

        if (read.DroppedCount > 0)
        {
            AddError(HudErrorCode.SessionEventOverflow, default, HudId.Empty, 0, read.DroppedCount);
        }

        if (read.State != HudSessionState.Open && read.State != _lastSessionState)
        {
            AddError(
                read.State == HudSessionState.Faulted ? HudErrorCode.SessionFaulted : HudErrorCode.SessionClosed,
                default,
                HudId.Empty,
                0,
                -1);
        }

        _lastSessionState = read.State;
        StableSortEvents(count);
        for (int index = 0; index < count; index++)
        {
            ApplyEvent(_eventBuffer[index]);
        }
    }

    private void StableSortEvents(int count)
    {
        for (int index = 1; index < count; index++)
        {
            HudEvent item = _eventBuffer[index];
            int insertion = index;
            while (insertion > 0 && item.Stamp.CompareTo(_eventBuffer[insertion - 1].Stamp) < 0)
            {
                _eventBuffer[insertion] = _eventBuffer[insertion - 1];
                insertion--;
            }

            _eventBuffer[insertion] = item;
        }
    }

    private void ApplyEvent(in HudEvent item)
    {
        switch (item.Kind)
        {
            case HudEventKind.ActionSlotChanged:
            case HudEventKind.ActionSlotCleared:
                ApplyAction(item);
                break;
            case HudEventKind.UnitChanged:
            case HudEventKind.UnitRemoved:
                ApplyUnit(item);
                break;
            case HudEventKind.FeedbackRaised:
                ApplyFeedback(item);
                break;
            case HudEventKind.FeedbackCancelled:
                ApplyFeedbackCancellation(item);
                break;
            case HudEventKind.QuestTracked:
            case HudEventKind.QuestUntracked:
                ApplyQuest(item);
                break;
            case HudEventKind.ChatReceived:
            case HudEventKind.ChatRemoved:
                ApplyChat(item);
                break;
            default:
                AddError(HudErrorCode.InvalidEvent, item.Stamp, item.EventId, item.EntityId, item.Slot);
                break;
        }
    }

    private void ApplyAction(in HudEvent item)
    {
        if ((uint)item.Slot >= (uint)_actions.Length ||
            (item.Kind == HudEventKind.ActionSlotChanged && (item.ContentId.IsEmpty || item.Value < 0)))
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, item.ContentId, 0, item.Slot);
            return;
        }

        ref ActionState state = ref _actions[item.Slot];
        if (!AcceptAuthority(state.HasAuthority, state.Stamp, state.LastEvent, item, item.ContentId, 0, item.Slot))
        {
            return;
        }

        state.HasAuthority = true;
        state.Stamp = item.Stamp;
        state.LastEvent = item;
        state.AbilityId = item.Kind == HudEventKind.ActionSlotCleared ? HudId.Empty : item.ContentId;
        state.CooldownMilliseconds = item.Kind == HudEventKind.ActionSlotCleared ? 0 : item.Value;
        state.Enabled = item.Kind == HudEventKind.ActionSlotChanged && item.Flag;
        UpdateActionView(item.Slot);
        EmitAction(item.Slot);
    }

    private void ApplyUnit(in HudEvent item)
    {
        if (item.EntityId == 0 || (item.Kind == HudEventKind.UnitChanged &&
            (item.ContentId.IsEmpty || item.Value < 0 || item.Auxiliary <= 0 || item.Value > item.Auxiliary)))
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, item.ContentId, item.EntityId, -1);
            return;
        }

        int index = FindEntity(item.EntityId);
        if (index < 0)
        {
            index = FindFreeEntity();
            if (index < 0)
            {
                AddError(HudErrorCode.EntityCapacityExceeded, item.Stamp, item.ContentId, item.EntityId, -1);
                return;
            }

            _entities[index].EntityId = item.EntityId;
            _entities[index].Occupied = true;
        }

        ref EntityState state = ref _entities[index];
        if (!AcceptAuthority(state.HasAuthority, state.Stamp, state.LastEvent, item, item.ContentId, item.EntityId, -1))
        {
            return;
        }

        bool wasActive = state.HasAuthority && !state.Removed;
        HudId previousName = state.Name;
        int previousHealth = state.Health;
        int previousMaximumHealth = state.MaximumHealth;
        HudPlateAssignment previousPlate = state.Presentation.Plate;
        bool previousOvertip = state.Presentation.OvertipCandidate;
        state.HasAuthority = true;
        state.Stamp = item.Stamp;
        state.LastEvent = item;
        state.Removed = item.Kind == HudEventKind.UnitRemoved;
        state.Name = state.Removed ? HudId.Empty : item.ContentId;
        state.Health = state.Removed ? 0 : item.Value;
        state.MaximumHealth = state.Removed ? 0 : item.Auxiliary;
        state.Presentation = state.Removed ? default : item.UnitPresentation;

        if (state.Removed)
        {
            ReleaseUnitPresentation(index, item.Stamp, HudUnitChangeAreas.Removal | HudUnitChangeAreas.Assignment | HudUnitChangeAreas.Visibility);
            CancelFeedbackForEntity(item.EntityId);
            UpdateUnitView(index);
            return;
        }

        ReconcileUnitPresentation(index, item.UnitPresentation, item.Stamp);
        HudUnitChangeAreas areas = HudUnitChangeAreas.None;
        if (!wasActive || previousName != state.Name)
        {
            areas |= HudUnitChangeAreas.Identity;
        }

        if (!wasActive || previousHealth != state.Health || previousMaximumHealth != state.MaximumHealth)
        {
            areas |= HudUnitChangeAreas.Vitality;
        }

        if (!wasActive || previousPlate != state.Presentation.Plate || previousOvertip != state.Presentation.OvertipCandidate)
        {
            areas |= HudUnitChangeAreas.Assignment | HudUnitChangeAreas.Visibility;
        }

        UpdateUnitView(index);
        EmitUnitChanges(index, areas, item.Stamp);
    }

    private void ReconcileUnitPresentation(int entityIndex, HudUnitPresentation presentation, HudStamp stamp)
    {
        ref EntityState entity = ref _entities[entityIndex];
        int desiredPlate = presentation.Plate.IsNone ? -1 : FindUnitPlate(presentation.Plate);
        if (!presentation.Plate.IsNone && desiredPlate < 0)
        {
            AddError(HudErrorCode.InvalidEvent, stamp, presentation.Plate.SemanticId, entity.EntityId, entityIndex);
            entity.Presentation = new HudUnitPresentation(HudPlateAssignment.None, presentation.OvertipCandidate);
        }

        if (entity.PlateIndex != desiredPlate)
        {
            ReleasePlate(entityIndex, stamp, HudUnitChangeAreas.Assignment | HudUnitChangeAreas.Visibility);
            if (desiredPlate >= 0)
            {
                ref UnitPlateState plate = ref _unitPlates[desiredPlate];
                if (plate.Occupied && plate.EntityIndex != entityIndex)
                {
                    int order = stamp.CompareTo(plate.Stamp);
                    if (order <= 0)
                    {
                        AddError(HudErrorCode.UnitPlateAssignmentConflict, stamp, plate.Assignment.SemanticId, entity.EntityId, entityIndex);
                        desiredPlate = -1;
                        entity.Presentation = new HudUnitPresentation(HudPlateAssignment.None, presentation.OvertipCandidate);
                    }
                    else
                    {
                        int displaced = plate.EntityIndex;
                        _entities[displaced].PlateIndex = -1;
                        _entities[displaced].Presentation = new HudUnitPresentation(
                            HudPlateAssignment.None,
                            _entities[displaced].Presentation.OvertipCandidate);
                        UpdateUnitView(displaced);
                        EmitPlate(displaced, desiredPlate, false, HudUnitChangeAreas.Assignment | HudUnitChangeAreas.Visibility, stamp);
                    }
                }

                if (desiredPlate >= 0)
                {
                    plate.Occupied = true;
                    plate.EntityIndex = entityIndex;
                    plate.EntityId = entity.EntityId;
                    plate.Stamp = stamp;
                    entity.PlateIndex = desiredPlate;
                    UpdateUnitPlateView(desiredPlate);
                }
            }
        }
        else if (desiredPlate >= 0)
        {
            _unitPlates[desiredPlate].Stamp = stamp;
            UpdateUnitPlateView(desiredPlate);
        }

        if (!presentation.OvertipCandidate)
        {
            ReleaseOvertip(entityIndex, stamp, HudUnitChangeAreas.Assignment | HudUnitChangeAreas.Visibility);
        }
        else if (entity.OvertipIndex < 0)
        {
            int free = FindFreeOvertip();
            if (free < 0)
            {
                AddError(HudErrorCode.OvertipCapacityExceeded, stamp, HudId.Empty, entity.EntityId, entityIndex);
            }
            else
            {
                ref OvertipState overtip = ref _overtips[free];
                overtip.Occupied = true;
                overtip.EntityIndex = entityIndex;
                overtip.EntityId = entity.EntityId;
                overtip.Stamp = stamp;
                overtip.Projected = false;
                overtip.Position = default;
                entity.OvertipIndex = free;
                UpdateOvertipView(free);
            }
        }
        else
        {
            _overtips[entity.OvertipIndex].Stamp = stamp;
            UpdateOvertipView(entity.OvertipIndex);
        }
    }

    private void ReleaseUnitPresentation(int entityIndex, HudStamp stamp, HudUnitChangeAreas areas)
    {
        ReleasePlate(entityIndex, stamp, areas);
        ReleaseOvertip(entityIndex, stamp, areas);
    }

    private void ReleasePlate(int entityIndex, HudStamp stamp, HudUnitChangeAreas areas)
    {
        ref EntityState entity = ref _entities[entityIndex];
        int plateIndex = entity.PlateIndex;
        if (plateIndex < 0)
        {
            return;
        }

        EmitPlate(entityIndex, plateIndex, false, areas, stamp);
        _unitPlates[plateIndex].Occupied = false;
        _unitPlates[plateIndex].EntityIndex = -1;
        _unitPlates[plateIndex].EntityId = 0;
        entity.PlateIndex = -1;
        UpdateUnitPlateView(plateIndex);
    }

    private void ReleaseOvertip(int entityIndex, HudStamp stamp, HudUnitChangeAreas areas)
    {
        ref EntityState entity = ref _entities[entityIndex];
        int overtipIndex = entity.OvertipIndex;
        if (overtipIndex < 0)
        {
            return;
        }

        EmitOvertip(entityIndex, overtipIndex, false, areas, stamp);
        _overtips[overtipIndex].Occupied = false;
        _overtips[overtipIndex].EntityIndex = -1;
        _overtips[overtipIndex].EntityId = 0;
        _overtips[overtipIndex].Projected = false;
        _overtips[overtipIndex].Position = default;
        entity.OvertipIndex = -1;
        UpdateOvertipView(overtipIndex);
    }

    private int FindUnitPlate(HudPlateAssignment assignment)
    {
        for (int index = 0; index < _unitPlates.Length; index++)
        {
            if (_unitPlates[index].Assignment == assignment)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindFreeOvertip()
    {
        for (int index = 0; index < _overtips.Length; index++)
        {
            if (!_overtips[index].Occupied)
            {
                return index;
            }
        }

        return -1;
    }

    private bool AcceptAuthority(
        bool hasAuthority,
        HudStamp currentStamp,
        in HudEvent current,
        in HudEvent next,
        HudId related,
        ulong entityId,
        int index)
    {
        if (!hasAuthority)
        {
            return true;
        }

        int order = next.Stamp.CompareTo(currentStamp);
        if (order < 0)
        {
            AddError(HudErrorCode.StaleAuthority, next.Stamp, related, entityId, index);
            return false;
        }

        if (order > 0)
        {
            return true;
        }

        if (!next.PayloadEquals(current))
        {
            AddError(HudErrorCode.AuthorityConflict, next.Stamp, related, entityId, index);
        }

        return false;
    }

    private void ApplyFeedback(in HudEvent item)
    {
        if (item.EventId.IsEmpty || item.EntityId == 0 || item.Value == 0 || (uint)item.FeedbackKind >= 3)
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, item.EventId, item.EntityId, -1);
            return;
        }

        int history = FindTransient(item.EventId);
        if (history >= 0)
        {
            ref TransientState previous = ref _transients[history];
            int order = item.Stamp.CompareTo(previous.Stamp);
            if (order < 0)
            {
                AddError(HudErrorCode.StaleAuthority, item.Stamp, item.EventId, item.EntityId, -1);
                return;
            }

            if (order == 0)
            {
                if (!item.PayloadEquals(previous.LastEvent))
                {
                    AddError(HudErrorCode.AuthorityConflict, item.Stamp, item.EventId, item.EntityId, -1);
                }

                return;
            }

            CancelFeedbackByEvent(item.EventId);
            previous.Stamp = item.Stamp;
            previous.LastEvent = item;
        }
        else
        {
            history = RememberTransient(item);
        }

        int start = (int)item.FeedbackKind * HudProduct.FeedbackPoolCount;
        int selected = -1;
        long oldest = long.MaxValue;
        for (int lane = 0; lane < HudProduct.FeedbackPoolCount; lane++)
        {
            int candidate = start + lane;
            ref FeedbackState slot = ref _feedback[candidate];
            if (!slot.Active)
            {
                selected = candidate;
                break;
            }

            if (slot.StartedAt < oldest)
            {
                oldest = slot.StartedAt;
                selected = candidate;
            }
        }

        ref FeedbackState target = ref _feedback[selected];
        target.Generation++;
        target.EventId = item.EventId;
        target.EntityId = item.EntityId;
        target.Amount = item.Value;
        target.Critical = item.Flag;
        target.Active = true;
        target.Visible = true;
        target.Projected = false;
        target.Position = default;
        target.StartedAt = _lastNow;
        target.VisibleUntil = checked(_lastNow + _product.Timelines.VisibleFor(item.FeedbackKind));
        target.ExpiresAt = checked(_lastNow + _product.Timelines.ActiveFor(item.FeedbackKind));
        _transients[history].FeedbackIndex = selected;
        UpdateFeedbackView(selected);
        EmitFeedback(selected, true);
    }

    private void ApplyFeedbackCancellation(in HudEvent item)
    {
        if (item.EventId.IsEmpty)
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, item.EventId, 0, -1);
            return;
        }

        int history = FindTransient(item.EventId);
        if (history < 0)
        {
            RememberTransient(item);
            return;
        }

        ref TransientState previous = ref _transients[history];
        int order = item.Stamp.CompareTo(previous.Stamp);
        if (order < 0)
        {
            AddError(HudErrorCode.StaleAuthority, item.Stamp, item.EventId, 0, -1);
            return;
        }

        if (order == 0)
        {
            if (!item.PayloadEquals(previous.LastEvent))
            {
                AddError(HudErrorCode.AuthorityConflict, item.Stamp, item.EventId, 0, -1);
            }

            return;
        }

        CancelFeedbackByEvent(item.EventId);
        previous.Stamp = item.Stamp;
        previous.LastEvent = item;
        previous.FeedbackIndex = -1;
    }

    private void ApplyQuest(in HudEvent item)
    {
        HudId questId = item.ContentId;
        if (questId.IsEmpty || (item.Kind == HudEventKind.QuestTracked &&
            (item.Quest is null || item.Quest.QuestId != questId)))
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, questId, 0, -1);
            return;
        }

        int index = FindQuest(questId);
        if (index >= 0)
        {
            ref QuestState current = ref _quests[index];
            if (!AcceptAuthority(current.HasAuthority, current.Stamp, current.LastEvent, item, questId, 0, index))
            {
                return;
            }
        }
        else
        {
            int tombstoneIndex = FindQuestTombstone(questId);
            if (tombstoneIndex >= 0)
            {
                ref QuestTombstone tombstone = ref _questTombstones[tombstoneIndex];
                if (!AcceptAuthority(true, tombstone.Stamp, tombstone.LastEvent, item, questId, 0, -1))
                {
                    return;
                }
            }

            if (item.Kind == HudEventKind.QuestTracked)
            {
                index = FindFreeQuest();
                if (index < 0)
                {
                    AddError(HudErrorCode.QuestCapacityExceeded, item.Stamp, questId, 0, -1);
                    return;
                }
            }
        }

        if (item.Kind == HudEventKind.QuestUntracked)
        {
            RememberQuestTombstone(item);
            if (index >= 0)
            {
                ref QuestState removed = ref _quests[index];
                removed.HasAuthority = false;
                removed.Tracked = false;
                removed.QuestId = HudId.Empty;
                removed.Snapshot = null;
                UpdateQuestView(index);
                _diff.AddChange(new HudChange(
                    HudChangeKind.QuestTracker,
                    removed.Element,
                    0,
                    false,
                    0,
                    false,
                    questId,
                    default));
            }

            return;
        }

        ref QuestState state = ref _quests[index];
        state.HasAuthority = true;
        state.QuestId = questId;
        state.Stamp = item.Stamp;
        state.LastEvent = item;
        state.Tracked = true;
        state.Snapshot = item.Quest;
        UpdateQuestView(index);
        _diff.AddChange(new HudChange(
            HudChangeKind.QuestTracker,
            state.Element,
            0,
            state.Tracked,
            state.Snapshot?.Objectives.Length ?? 0,
            state.Snapshot?.Completable ?? false,
            state.Snapshot?.TitleId ?? HudId.Empty,
            default));
    }

    private void ApplyChat(in HudEvent item)
    {
        if (item.EventId.IsEmpty || (item.Kind == HudEventKind.ChatReceived && item.Chat is null))
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, item.EventId, item.EntityId, -1);
            return;
        }

        int index = FindChat(item.EventId);
        if (index >= 0)
        {
            ref ChatState current = ref _chat[index];
            if (!AcceptAuthority(current.HasAuthority, current.Stamp, current.LastEvent, item, item.EventId, item.EntityId, index))
            {
                return;
            }
        }
        else
        {
            index = FindFreeChat();
            if (index < 0)
            {
                index = _chatCursor;
                _chatCursor = (_chatCursor + 1) % _chat.Length;
            }
        }

        ref ChatState state = ref _chat[index];
        state.Occupied = true;
        state.HasAuthority = true;
        state.EventId = item.EventId;
        state.Stamp = item.Stamp;
        state.LastEvent = item;
        state.Message = item.Kind == HudEventKind.ChatReceived ? item.Chat : null;
        state.Active = state.Message is not null;
        state.Projected = false;
        state.Position = default;
        UpdateChatView(index);
        _diff.AddChange(new HudChange(
            HudChangeKind.Chat,
            item.EventId,
            0,
            state.Active,
            0,
            state.Message?.WorldBubble ?? false,
            state.Message?.ChannelId ?? HudId.Empty,
            default));
    }

    private int RememberTransient(in HudEvent item)
    {
        int index = _transientCursor;
        _transientCursor = (_transientCursor + 1) % _transients.Length;
        ref TransientState record = ref _transients[index];
        if (record.Occupied && record.FeedbackIndex >= 0 &&
            _feedback[record.FeedbackIndex].Active &&
            _feedback[record.FeedbackIndex].EventId == record.EventId)
        {
            CancelFeedback(record.FeedbackIndex);
        }

        record.Occupied = true;
        record.EventId = item.EventId;
        record.Stamp = item.Stamp;
        record.LastEvent = item;
        record.FeedbackIndex = -1;
        return index;
    }

    private void CancelFeedbackByEvent(HudId eventId)
    {
        for (int index = 0; index < _feedback.Length; index++)
        {
            if (_feedback[index].Active && _feedback[index].EventId == eventId)
            {
                CancelFeedback(index);
                return;
            }
        }
    }

    private void CancelFeedbackForEntity(ulong entityId)
    {
        for (int index = 0; index < _feedback.Length; index++)
        {
            if (_feedback[index].Active && _feedback[index].EntityId == entityId)
            {
                CancelFeedback(index);
            }
        }
    }

    private void CancelFeedback(int index)
    {
        ref FeedbackState slot = ref _feedback[index];
        if (!slot.Active)
        {
            return;
        }

        slot.Generation++;
        slot.Active = false;
        slot.Visible = false;
        slot.Projected = false;
        UpdateFeedbackView(index);
        EmitFeedback(index, false);
    }

    private void AdvanceFeedback(long now, HudViewport viewport)
    {
        for (int index = 0; index < _feedback.Length; index++)
        {
            ref FeedbackState state = ref _feedback[index];
            if (!state.Active)
            {
                continue;
            }

            if (now >= state.ExpiresAt)
            {
                state.Active = false;
                state.Projected = false;
                UpdateFeedbackView(index);
                continue;
            }

            if (state.Visible && now >= state.VisibleUntil)
            {
                state.Visible = false;
                UpdateFeedbackView(index);
                EmitFeedback(index, false);
            }

            bool projected = false;
            HudPoint point = default;
            if (viewport.IsValid && _world.TryProject(new HudWorldQuery(state.EntityId, viewport), out HudProjection projection))
            {
                point = projection.Screen;
                projected = projection.InFrustum && !projection.Occluded && projection.Depth > 0 &&
                    double.IsFinite(projection.Depth) && viewport.Contains(point);
            }

            if (projected != state.Projected || (projected && point != state.Position))
            {
                state.Projected = projected;
                state.Position = projected ? point : default;
                UpdateFeedbackView(index);
                _diff.AddChange(new HudChange(
                    HudChangeKind.Projection,
                    state.Element,
                    state.Generation,
                    projected,
                    0,
                    false,
                    HudId.Empty,
                    state.Position));
            }
        }
    }

    private void AdvanceWorldChat(HudViewport viewport)
    {
        for (int index = 0; index < _chat.Length; index++)
        {
            ref ChatState state = ref _chat[index];
            if (!state.Active || state.Message is null || !state.Message.WorldBubble)
            {
                continue;
            }

            bool projected = false;
            HudPoint point = default;
            if (viewport.IsValid && _world.TryProject(new HudWorldQuery(state.Message.SenderEntityId, viewport), out HudProjection projection))
            {
                point = projection.Screen;
                projected = projection.InFrustum && !projection.Occluded && projection.Depth > 0 &&
                    double.IsFinite(projection.Depth) && viewport.Contains(point);
            }

            if (state.Projected != projected || (projected && state.Position != point))
            {
                state.Projected = projected;
                state.Position = projected ? point : default;
                UpdateChatView(index);
                _diff.AddChange(new HudChange(
                    HudChangeKind.WorldChatProjection,
                    state.EventId,
                    0,
                    projected,
                    0,
                    true,
                    state.Message.ChannelId,
                    state.Position));
            }
        }
    }

    private void AdvanceOvertips(HudViewport viewport)
    {
        for (int index = 0; index < _overtips.Length; index++)
        {
            ref OvertipState overtip = ref _overtips[index];
            if (!overtip.Occupied)
            {
                continue;
            }

            bool projected = false;
            HudPoint point = default;
            if (viewport.IsValid && _world.TryProject(new HudWorldQuery(overtip.EntityId, viewport), out HudProjection projection))
            {
                point = projection.Screen;
                projected = projection.InFrustum && !projection.Occluded && projection.Depth > 0 &&
                    double.IsFinite(projection.Depth) && viewport.Contains(point);
            }

            if (overtip.Projected != projected || (projected && overtip.Position != point))
            {
                overtip.Projected = projected;
                overtip.Position = projected ? point : default;
                UpdateUnitView(overtip.EntityIndex);
                UpdateOvertipView(index);
                EmitOvertip(
                    overtip.EntityIndex,
                    index,
                    projected,
                    HudUnitChangeAreas.Visibility | HudUnitChangeAreas.Projection,
                    _entities[overtip.EntityIndex].Stamp);
            }
        }
    }

    private void EmitUnitChanges(int entityIndex, HudUnitChangeAreas areas, HudStamp revision)
    {
        ref EntityState entity = ref _entities[entityIndex];
        if (entity.PlateIndex >= 0)
        {
            UpdateUnitPlateView(entity.PlateIndex);
            EmitPlate(entityIndex, entity.PlateIndex, true, areas, revision);
        }

        if (entity.OvertipIndex >= 0)
        {
            UpdateOvertipView(entity.OvertipIndex);
            EmitOvertip(entityIndex, entity.OvertipIndex, _overtips[entity.OvertipIndex].Projected, areas, revision);
        }
    }

    private void EmitPlate(int entityIndex, int plateIndex, bool visible, HudUnitChangeAreas areas, HudStamp revision)
    {
        ref EntityState entity = ref _entities[entityIndex];
        ref UnitPlateState plate = ref _unitPlates[plateIndex];
        _diff.AddChange(new HudChange(
            HudChangeKind.UnitPlate,
            plate.Element,
            0,
            visible,
            entity.Health,
            false,
            entity.Name,
            default,
            entity.MaximumHealth,
            revision,
            areas));
    }

    private void EmitOvertip(int entityIndex, int overtipIndex, bool visible, HudUnitChangeAreas areas, HudStamp revision)
    {
        ref EntityState entity = ref _entities[entityIndex];
        ref OvertipState overtip = ref _overtips[overtipIndex];
        _diff.AddChange(new HudChange(
            HudChangeKind.Overtip,
            overtip.Element,
            overtipIndex,
            visible,
            entity.Health,
            false,
            entity.Name,
            overtip.Position,
            entity.MaximumHealth,
            revision,
            areas));
    }

    private void DrainInput()
    {
        while (_inputCount > 0)
        {
            HudInput input = _inputQueue[_inputHead];
            _inputHead = (_inputHead + 1) % _inputQueue.Length;
            _inputCount--;
            ApplyInput(input);
        }
    }

    private void ApplyInput(in HudInput input)
    {
        switch (input.Kind)
        {
            case HudInputKind.ActivateAction:
                ActivateAction(input.Slot);
                break;
            case HudInputKind.SelectWorldEntity:
                SendCommand(HudCommand.SelectWorldEntity(input.EntityId), HudCommandFamilies.SelectWorldEntity);
                break;
            case HudInputKind.InteractWorldEntity:
                SendCommand(HudCommand.InteractWorldEntity(input.EntityId), HudCommandFamilies.InteractWorldEntity);
                break;
            case HudInputKind.RequestFocus:
                RequestFocus(input.Focus);
                break;
            case HudInputKind.ReleaseFocus:
                if (_focus == input.Focus)
                {
                    SetFocus(HudFocus.World);
                }

                break;
            case HudInputKind.Cancel:
                SetFocus(HudFocus.World);
                _hoverElement = HudId.Empty;
                break;
            case HudInputKind.PointerMoved:
            case HudInputKind.PointerEntered:
                SetVirtualPointer(input.PointerSource, input.Pointer);
                _hoverElement = AcceptPointerTarget(input) ? input.Target : HudId.Empty;
                break;
            case HudInputKind.PointerExited:
                SetVirtualPointer(input.PointerSource, input.Pointer);
                _hoverElement = HudId.Empty;
                break;
            case HudInputKind.PointerPrimaryPressed:
            case HudInputKind.PointerPrimaryDoublePressed:
                SetVirtualPointer(input.PointerSource, input.Pointer);
                if (AcceptPointerTarget(input))
                {
                    _hoverElement = input.Target;
                    int slot = _product.FindActionSlot(input.Target);
                    if (slot >= 0)
                    {
                        ActivateAction(slot);
                    }
                    else
                    {
                        RequestFocus(HudFocus.Hud);
                    }
                }

                break;
            case HudInputKind.PointerPrimaryReleased:
            case HudInputKind.PointerSecondaryPressed:
            case HudInputKind.PointerSecondaryReleased:
            case HudInputKind.PointerSecondaryDoublePressed:
                SetVirtualPointer(input.PointerSource, input.Pointer);
                if (AcceptPointerTarget(input))
                {
                    _hoverElement = input.Target;
                }

                break;
            case HudInputKind.DragStarted:
                SetVirtualPointer(input.PointerSource, input.Pointer);
                if (AcceptPointerTarget(input))
                {
                    _hoverElement = input.Target;
                    RequestFocus(HudFocus.Drag);
                }

                break;
            case HudInputKind.DragEnded:
                SetVirtualPointer(input.PointerSource, input.Pointer);
                if (_focus == HudFocus.Drag)
                {
                    SetFocus(_focusBeforeDrag);
                }

                break;
            case HudInputKind.SubmitChat:
                SendCommand(HudCommand.SubmitChat(input.Text), HudCommandFamilies.SubmitChat);
                break;
        }
    }

    private void ActivateAction(int slot)
    {
        if ((uint)slot >= (uint)_actions.Length)
        {
            return;
        }

        ref ActionState state = ref _actions[slot];
        if (state.AbilityId.IsEmpty || !state.Enabled)
        {
            return;
        }

        SendCommand(HudCommand.ActivateAction(slot, state.AbilityId), HudCommandFamilies.ActivateAction);
    }

    private void SendCommand(in HudCommand command, HudCommandFamilies family)
    {
        if ((_session.Capabilities.Commands & family) == 0)
        {
            AddError(HudErrorCode.UnsupportedCommand, default, command.Value, command.EntityId, command.Slot);
            return;
        }

        if (!_session.TryWrite(command))
        {
            AddError(
                _lastSessionState == HudSessionState.Faulted ? HudErrorCode.SessionFaulted : HudErrorCode.CommandQueueFull,
                default,
                command.Value,
                command.EntityId,
                command.Slot);
        }
    }

    private bool AcceptPointerTarget(in HudInput input)
    {
        if (input.Target.IsEmpty || !input.Pointer.IsFinite)
        {
            return false;
        }

        if (!_product.RequiresPixelMask(input.Target))
        {
            return true;
        }

        return input.HasMaskSample && input.MaskPoint.IsFinite && float.IsFinite(input.MaskAlpha) &&
            input.MaskAlpha >= _product.PixelMaskThreshold;
    }

    private void RequestFocus(HudFocus focus)
    {
        if (focus >= _focus)
        {
            if (focus == HudFocus.Drag && _focus != HudFocus.Drag)
            {
                _focusBeforeDrag = _focus;
            }

            SetFocus(focus);
        }
    }

    private void SetFocus(HudFocus focus)
    {
        if (_focus == focus)
        {
            return;
        }

        _focus = focus;
        _diff.ReadModel.Focus = focus;
        _diff.AddChange(new HudChange(HudChangeKind.Focus, HudId.Empty, 0, true, (int)focus, false, HudId.Empty, default));
    }

    private void SetVirtualPointer(HudPointerSource source, HudPoint point)
    {
        if (_pointerSource == source && _pointer == point)
        {
            return;
        }

        _pointerSource = source;
        _pointer = point;
        _diff.ReadModel.PointerSource = source;
        _diff.ReadModel.Pointer = point;
        _diff.AddChange(new HudChange(HudChangeKind.VirtualPointer, HudId.Empty, 0, true, (int)source, false, HudId.Empty, point));
    }

    private void UpdateCursor()
    {
        HudCursor next = _focus switch
        {
            HudFocus.Drag => HudCursor.Drag,
            HudFocus.Chat => HudCursor.Text,
            _ when !_hoverElement.IsEmpty => HudCursor.Hover,
            _ => HudCursor.Default,
        };
        if (next == _cursor && !_firstFrame)
        {
            return;
        }

        _cursor = next;
        HudId cursorId = _product.Cursors.Resolve(next);
        _diff.ReadModel.CursorId = cursorId;
        _diff.AddChange(new HudChange(HudChangeKind.Cursor, HudId.Empty, 0, true, (int)next, false, cursorId, default));
    }

    private void EmitFullState()
    {
        for (int index = 0; index < _actions.Length; index++)
        {
            EmitAction(index);
        }

        for (int index = 0; index < _feedback.Length; index++)
        {
            EmitFeedback(index, false);
        }

        for (int index = 0; index < _unitPlates.Length; index++)
        {
            _diff.AddChange(new HudChange(
                HudChangeKind.UnitPlate,
                _unitPlates[index].Element,
                0,
                false,
                0,
                false,
                HudId.Empty,
                default,
                0,
                default,
                HudUnitChangeAreas.All));
        }

        for (int index = 0; index < _overtips.Length; index++)
        {
            _diff.AddChange(new HudChange(
                HudChangeKind.Overtip,
                _overtips[index].Element,
                index,
                false,
                0,
                false,
                HudId.Empty,
                default,
                0,
                default,
                HudUnitChangeAreas.All));
        }

        _diff.AddChange(new HudChange(HudChangeKind.Focus, HudId.Empty, 0, true, (int)_focus, false, HudId.Empty, default));
        HudId cursorId = _product.Cursors.Resolve(_cursor);
        _diff.ReadModel.CursorId = cursorId;
        _diff.AddChange(new HudChange(HudChangeKind.Cursor, HudId.Empty, 0, true, (int)_cursor, false, cursorId, default));
    }

    private void EmitAction(int index)
    {
        ref ActionState state = ref _actions[index];
        _diff.AddChange(new HudChange(
            HudChangeKind.ActionSlot,
            state.Element,
            0,
            !state.AbilityId.IsEmpty,
            state.CooldownMilliseconds,
            state.Enabled,
            state.AbilityId,
            default));
    }

    private void EmitFeedback(int index, bool visible)
    {
        ref FeedbackState state = ref _feedback[index];
        _diff.AddChange(new HudChange(
            HudChangeKind.Feedback,
            state.Element,
            state.Generation,
            visible,
            state.Amount,
            state.Critical,
            state.EventId,
            state.Position));
    }

    private void UpdateActionView(int index)
    {
        ref ActionState state = ref _actions[index];
        _actionViews[index] = new HudActionSlotView(
            state.Element,
            state.AbilityId,
            state.CooldownMilliseconds,
            state.Enabled,
            state.Stamp,
            state.HasAuthority);
    }

    private void UpdateFeedbackView(int index)
    {
        ref FeedbackState state = ref _feedback[index];
        _feedbackViews[index] = new HudFeedbackView(
            state.Element,
            state.EventId,
            state.Kind,
            state.EntityId,
            state.Amount,
            state.Critical,
            state.Generation,
            state.Active,
            state.Projected,
            state.Position);
    }

    private void UpdateUnitView(int index)
    {
        ref EntityState entity = ref _entities[index];
        HudId plateElement = HudId.Empty;
        bool plateVisible = false;
        if (entity.PlateIndex >= 0)
        {
            plateElement = _unitPlates[entity.PlateIndex].Element;
            plateVisible = !entity.Removed;
        }

        HudId overtipElement = HudId.Empty;
        bool overtipVisible = false;
        HudPoint overtipPosition = default;
        if (entity.OvertipIndex >= 0)
        {
            ref OvertipState overtip = ref _overtips[entity.OvertipIndex];
            overtipElement = overtip.Element;
            overtipVisible = !entity.Removed && overtip.Projected;
            overtipPosition = overtipVisible ? overtip.Position : default;
        }

        _unitViews[index] = new HudUnitView(
            entity.EntityId,
            entity.Name,
            entity.Health,
            entity.MaximumHealth,
            entity.Stamp,
            entity.Occupied && entity.HasAuthority && !entity.Removed,
            entity.Presentation.Plate,
            plateElement,
            plateVisible,
            entity.Presentation.OvertipCandidate,
            overtipElement,
            overtipVisible,
            overtipPosition);
    }

    private void UpdateUnitPlateView(int index)
    {
        ref UnitPlateState plate = ref _unitPlates[index];
        if (!plate.Occupied)
        {
            _unitPlateViews[index] = new HudUnitPlateView(
                plate.Element,
                plate.Assignment,
                0,
                HudId.Empty,
                0,
                0,
                plate.Stamp,
                false,
                false);
            return;
        }

        ref EntityState entity = ref _entities[plate.EntityIndex];
        _unitPlateViews[index] = new HudUnitPlateView(
            plate.Element,
            plate.Assignment,
            entity.EntityId,
            entity.Name,
            entity.Health,
            entity.MaximumHealth,
            entity.Stamp,
            true,
            !entity.Removed);
    }

    private void UpdateOvertipView(int index)
    {
        ref OvertipState overtip = ref _overtips[index];
        if (!overtip.Occupied)
        {
            _overtipViews[index] = new HudOvertipView(
                overtip.Element,
                index,
                0,
                HudId.Empty,
                0,
                0,
                overtip.Stamp,
                false,
                false,
                default);
            return;
        }

        ref EntityState entity = ref _entities[overtip.EntityIndex];
        _overtipViews[index] = new HudOvertipView(
            overtip.Element,
            index,
            entity.EntityId,
            entity.Name,
            entity.Health,
            entity.MaximumHealth,
            entity.Stamp,
            true,
            !entity.Removed && overtip.Projected,
            !entity.Removed && overtip.Projected ? overtip.Position : default);
    }

    private int FindEntity(ulong entityId)
    {
        for (int index = 0; index < _entities.Length; index++)
        {
            if (_entities[index].Occupied && _entities[index].EntityId == entityId)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindFreeEntity()
    {
        for (int index = 0; index < _entities.Length; index++)
        {
            if (!_entities[index].Occupied)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindTransient(HudId eventId)
    {
        for (int index = 0; index < _transients.Length; index++)
        {
            if (_transients[index].Occupied && _transients[index].EventId == eventId)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindQuest(HudId questId)
    {
        for (int index = 0; index < _quests.Length; index++)
        {
            if (_quests[index].Tracked && _quests[index].QuestId == questId)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindFreeQuest()
    {
        for (int index = 0; index < _quests.Length; index++)
        {
            if (!_quests[index].Tracked)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindQuestTombstone(HudId questId)
    {
        for (int index = 0; index < _questTombstones.Length; index++)
        {
            if (_questTombstones[index].Occupied && _questTombstones[index].QuestId == questId)
            {
                return index;
            }
        }

        return -1;
    }

    private void RememberQuestTombstone(in HudEvent item)
    {
        int index = FindQuestTombstone(item.ContentId);
        if (index < 0)
        {
            index = _questTombstoneCursor;
            _questTombstoneCursor = (_questTombstoneCursor + 1) % _questTombstones.Length;
        }

        _questTombstones[index] = new QuestTombstone
        {
            Occupied = true,
            QuestId = item.ContentId,
            Stamp = item.Stamp,
            LastEvent = item,
        };
    }

    private void UpdateQuestView(int index)
    {
        ref QuestState state = ref _quests[index];
        _questViews[index] = new HudQuestView(
            state.Element,
            state.QuestId,
            state.Snapshot?.TitleId ?? HudId.Empty,
            state.Snapshot?.Completable ?? false,
            state.Tracked,
            state.Stamp,
            state.Snapshot);
    }

    private int FindChat(HudId eventId)
    {
        for (int index = 0; index < _chat.Length; index++)
        {
            if (_chat[index].Occupied && _chat[index].EventId == eventId)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindFreeChat()
    {
        for (int index = 0; index < _chat.Length; index++)
        {
            if (!_chat[index].Occupied)
            {
                return index;
            }
        }

        return -1;
    }

    private void UpdateChatView(int index)
    {
        ref ChatState state = ref _chat[index];
        _chatViews[index] = new HudChatView(
            state.EventId,
            state.Message?.ChannelId ?? HudId.Empty,
            state.Message?.SenderEntityId ?? 0,
            state.Message?.SenderNameId ?? HudId.Empty,
            state.Message?.Text,
            state.Message?.WorldBubble ?? false,
            state.Active,
            state.Projected,
            state.Position,
            state.Stamp);
    }

    private void AddError(HudErrorCode code, HudStamp stamp, HudId related, ulong entityId, int index) =>
        _diff.AddError(new HudError(code, stamp, related, entityId, index));

    private static bool IsValidInput(in HudInput input) => input.Kind switch
    {
        HudInputKind.ActivateAction => input.Slot >= 0,
        HudInputKind.SelectWorldEntity or HudInputKind.InteractWorldEntity => input.EntityId != 0,
        HudInputKind.RequestFocus or HudInputKind.ReleaseFocus => (uint)input.Focus <= (uint)HudFocus.Drag,
        HudInputKind.SubmitChat => !input.Text.IsEmpty,
        HudInputKind.Cancel or >= HudInputKind.PointerMoved and <= HudInputKind.DragEnded => true,
        _ => false,
    };

    private static bool IsPointerButton(HudInputKind kind) => kind is
        HudInputKind.PointerPrimaryPressed or HudInputKind.PointerPrimaryReleased or
        HudInputKind.PointerPrimaryDoublePressed or HudInputKind.PointerSecondaryPressed or
        HudInputKind.PointerSecondaryReleased or HudInputKind.PointerSecondaryDoublePressed or
        HudInputKind.DragStarted or HudInputKind.DragEnded;

    private struct ActionState
    {
        public HudId Element;
        public HudId AbilityId;
        public int CooldownMilliseconds;
        public bool Enabled;
        public bool HasAuthority;
        public HudStamp Stamp;
        public HudEvent LastEvent;
    }

    private struct FeedbackState
    {
        public HudId Element;
        public HudId EventId;
        public HudFeedbackKind Kind;
        public ulong EntityId;
        public int Amount;
        public bool Critical;
        public int Generation;
        public bool Active;
        public bool Visible;
        public bool Projected;
        public HudPoint Position;
        public long StartedAt;
        public long VisibleUntil;
        public long ExpiresAt;
    }

    private struct EntityState
    {
        public bool Occupied;
        public ulong EntityId;
        public bool HasAuthority;
        public HudStamp Stamp;
        public HudEvent LastEvent;
        public bool Removed;
        public HudId Name;
        public int Health;
        public int MaximumHealth;
        public HudUnitPresentation Presentation;
        public int PlateIndex;
        public int OvertipIndex;
    }

    private struct UnitPlateState
    {
        public HudPlateAssignment Assignment;
        public HudId Element;
        public bool Occupied;
        public int EntityIndex;
        public ulong EntityId;
        public HudStamp Stamp;
    }

    private struct OvertipState
    {
        public HudId Element;
        public bool Occupied;
        public int EntityIndex;
        public ulong EntityId;
        public HudStamp Stamp;
        public bool Projected;
        public HudPoint Position;
    }

    private struct TransientState
    {
        public bool Occupied;
        public HudId EventId;
        public HudStamp Stamp;
        public HudEvent LastEvent;
        public int FeedbackIndex;
    }

    private struct QuestState
    {
        public HudId Element;
        public HudId QuestId;
        public bool HasAuthority;
        public HudStamp Stamp;
        public HudEvent LastEvent;
        public bool Tracked;
        public HudQuestSnapshot? Snapshot;
    }

    private struct QuestTombstone
    {
        public bool Occupied;
        public HudId QuestId;
        public HudStamp Stamp;
        public HudEvent LastEvent;
    }

    private struct ChatState
    {
        public bool Occupied;
        public bool HasAuthority;
        public HudId EventId;
        public HudStamp Stamp;
        public HudEvent LastEvent;
        public HudChatMessage? Message;
        public bool Active;
        public bool Projected;
        public HudPoint Position;
    }
}
