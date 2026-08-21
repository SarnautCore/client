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
    private readonly HudDiff _diff;
    private int _inputHead;
    private int _inputCount;
    private int _transientCursor;
    private int _chatCursor;
    private int _questTombstoneCursor;
    private int _pendingInputOverflows;
    private long _lastNow;
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

        var readModel = new HudReadModel(_actionViews, _feedbackViews, _questViews, _chatViews)
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
        UpdateCursor();
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

        state.HasAuthority = true;
        state.Stamp = item.Stamp;
        state.LastEvent = item;
        state.Removed = item.Kind == HudEventKind.UnitRemoved;
        state.Name = state.Removed ? HudId.Empty : item.ContentId;
        state.Health = state.Removed ? 0 : item.Value;
        state.MaximumHealth = state.Removed ? 0 : item.Auxiliary;
        if (state.Removed)
        {
            CancelFeedbackForEntity(item.EntityId);
        }
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
