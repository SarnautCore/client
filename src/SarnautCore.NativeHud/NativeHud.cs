namespace SarnautCore.NativeHud;

/// <summary>
/// Engine-neutral HUD state machine. The runtime owns ordering, stable pools, timelines,
/// projection policy, focus, cursor selection, and bounded delivery.
/// </summary>
public sealed class NativeHud : IDisposable
{
    private static readonly HudId QuestAbandonHeaderId = new("hud.quest.abandon.header");
    private static readonly HudId QuestAbandonBodyId = new("hud.quest.abandon.body");
    private static readonly HudId QuestShareHeaderId = new("hud.quest.share.header");
    private static readonly HudId QuestShareBodyId = new("hud.quest.share.body");
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
    private readonly HudInventorySlotView[] _inventorySlotViews;
    private readonly InventoryCooldownState[] _inventoryCooldowns;
    private readonly HudInventoryPartitionView[] _inventoryPartitionViews;
    private readonly HudLootSlotView[] _lootSlotViews;
    private readonly HudQuestLogEntryView[] _questLogViews;
    private readonly HudCharacterEquipmentView[] _characterEquipmentViews;
    private readonly HudCharacterStatView[] _characterStatViews;
    private readonly HudQuestTalkOptionView[] _questTalkOptionViews;
    private readonly HudMessageBoxState[] _messageBoxes;
    private readonly HudMessageBoxView[] _messageBoxViews;
    private readonly HudInventoryReadModel _inventoryRead;
    private readonly HudLootReadModel _lootRead;
    private readonly HudQuestLogReadModel _questLogRead;
    private readonly HudCharacterReadModel _characterRead;
    private readonly HudQuestTalkOptionsReadModel _questTalkOptionsRead;
    private readonly HudMessageBoxReadModel _messageBoxRead;
    private readonly HudDiff _diff;
    private int _inputHead;
    private int _inputCount;
    private int _transientCursor;
    private int _chatCursor;
    private int _questTombstoneCursor;
    private long _messageBoxSequence;
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
    private bool _selectedTargetHasAuthority;
    private ulong _selectedTargetEntityId;
    private HudTargetSelectionRefusal _selectedTargetRefusal;
    private HudStamp _selectedTargetStamp;
    private HudEvent _selectedTargetEvent;
    private ContextState<HudInventorySnapshot> _inventory;
    private ContextState<HudLootSnapshot> _loot;
    private ContextState<HudQuestLogSnapshot> _questLog;
    private ContextState<HudQuestInfoSnapshot> _questInfo;
    private ContextState<HudCharacterSnapshot> _character;
    private HudId _selectedQuestId;
    private HudQuestLogBookmark _selectedQuestBookmark;
    private HudId _selectedQuestFolderId;
    private HudId _pendingAbandonQuestId;
    private long _abandonConfirmationExpiresAt;
    private long _shareInvitationExpiresAt;
    private long _shareOfferExpiresAt;
    private int _selectedRewardIndex = -1;
    private int _lootPage;
    private readonly HudContextWindow[] _openContextOrder = new HudContextWindow[6];
    private int _openContextCount;
    private bool _inventoryOpen;
    private bool _questLogOpen;
    private bool _questInfoOpen;
    private bool _characterOpen;
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
        _inventorySlotViews = new HudInventorySlotView[HudProduct.InventorySlotCount];
        _inventoryCooldowns = new InventoryCooldownState[HudProduct.InventorySlotCount];
        _inventoryPartitionViews = new HudInventoryPartitionView[HudProduct.InventoryPartitionCount];
        _lootSlotViews = new HudLootSlotView[HudProduct.LootPageSize];
        _questLogViews = new HudQuestLogEntryView[product.Contexts.QuestLog.MaxEntries];
        _characterEquipmentViews = new HudCharacterEquipmentView[HudProduct.CharacterEquipmentSlotCount];
        _characterStatViews = new HudCharacterStatView[HudProduct.CharacterStatCount];
        _questTalkOptionViews = new HudQuestTalkOptionView[HudProduct.QuestTalkOptionCount];
        _messageBoxes = new HudMessageBoxState[HudMessageBoxProduct.Capacity];
        _messageBoxViews = new HudMessageBoxView[HudMessageBoxProduct.Capacity];
        _inventoryRead = new HudInventoryReadModel(_inventorySlotViews, _inventoryPartitionViews);
        _lootRead = new HudLootReadModel(_lootSlotViews);
        _questLogRead = new HudQuestLogReadModel(_questLogViews);
        _characterRead = new HudCharacterReadModel(_characterEquipmentViews, _characterStatViews);
        _questTalkOptionsRead = new HudQuestTalkOptionsReadModel(_questTalkOptionViews);
        _messageBoxRead = new HudMessageBoxReadModel(_messageBoxViews);

        UpdateInventoryViews();
        UpdateLootViews();
        UpdateQuestLogViews();
        UpdateCharacterViews();
        UpdateQuestTalkOptionViews();
        UpdateMessageBoxViews();

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
            _overtipViews,
            _inventoryRead,
            _lootRead,
            _questLogRead,
            new HudQuestInfoView(product.Contexts.QuestInfo.InteractionRoot, product.Contexts.QuestInfo.DetailRoot,
                false, false, HudQuestInfoMode.None, HudId.Empty, 0,
                HudQuestRefusal.None, null, null, -1, -1, default, _questTalkOptionsRead),
            _characterRead,
            _messageBoxRead)
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
        AdvanceMessageBoxes(now);

        if (_shareOfferExpiresAt > 0 && now > _shareOfferExpiresAt)
        {
            _shareOfferExpiresAt = 0;
            UpdateQuestLogViews();
            EmitContext(HudChangeKind.QuestLog, _product.Contexts.QuestLog.Root, _questLogRead.Count, _questLogOpen, _questLog.Stamp);
        }

        DrainInput();
        AdvanceFeedback(now, frame.Viewport);
        AdvanceActionCooldowns(now);
        AdvanceInventoryCooldowns(now);
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
            _diff.RequireFullRefresh(HudRefreshAreas.All);
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
            case HudEventKind.TargetSelectionChanged:
                ApplyTargetSelection(item);
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
            case HudEventKind.InventoryReplaced:
                ApplyInventory(item);
                break;
            case HudEventKind.InventoryCooldownStarted:
            case HudEventKind.InventoryCooldownFinished:
                ApplyInventoryCooldown(item);
                break;
            case HudEventKind.LootReplaced:
                ApplyLoot(item);
                break;
            case HudEventKind.QuestLogReplaced:
                ApplyQuestLog(item);
                break;
            case HudEventKind.QuestInfoReplaced:
                ApplyQuestInfo(item);
                break;
            case HudEventKind.CharacterReplaced:
                ApplyCharacter(item);
                break;
            case HudEventKind.MessageBoxOffered:
            case HudEventKind.MessageBoxWithdrawn:
                ApplyMessageBox(item);
                break;
            default:
                AddError(HudErrorCode.InvalidEvent, item.Stamp, item.EventId, item.EntityId, item.Slot);
                break;
        }
    }

    private void ApplyAction(in HudEvent item)
    {
        if ((uint)item.Slot >= (uint)_actions.Length ||
            (item.Kind == HudEventKind.ActionSlotChanged &&
                (item.ContentId.IsEmpty || item.Value < 0 || item.Auxiliary < item.Value)))
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
        state.CooldownDurationMilliseconds = item.Kind == HudEventKind.ActionSlotCleared ? 0 : item.Auxiliary;
        state.CooldownReceivedAt = _lastNow;
        state.Enabled = item.Kind == HudEventKind.ActionSlotChanged && item.Flag;
        UpdateActionView(item.Slot);
        EmitAction(item.Slot);
    }

    private void ApplyTargetSelection(in HudEvent item)
    {
        if ((uint)item.Auxiliary > (uint)HudTargetSelectionRefusal.TargetDead ||
            item.Auxiliary == (int)HudTargetSelectionRefusal.Unspecified)
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, HudId.Empty, item.EntityId, -1);
            return;
        }

        if (!AcceptAuthority(
            _selectedTargetHasAuthority,
            _selectedTargetStamp,
            _selectedTargetEvent,
            item,
            HudId.Empty,
            item.EntityId,
            -1))
        {
            return;
        }

        _selectedTargetHasAuthority = true;
        _selectedTargetEntityId = item.EntityId;
        _selectedTargetRefusal = (HudTargetSelectionRefusal)item.Auxiliary;
        _selectedTargetStamp = item.Stamp;
        _selectedTargetEvent = item;
        _diff.ReadModel.SelectedTarget = new HudSelectedTargetView(
            item.EntityId, true, _selectedTargetRefusal, item.Stamp);
        _diff.AddChange(new HudChange(
            HudChangeKind.TargetSelection,
            HudId.Empty,
            0,
            item.EntityId != 0,
            0,
            true,
            HudId.Empty,
            default,
            Revision: item.Stamp));
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

    private void ApplyInventory(in HudEvent item)
    {
        HudInventorySnapshot? snapshot = item.Inventory;
        if (snapshot is null || !_product.Contexts.Inventory.TryFindLayout(snapshot.Capacity, out HudInventoryLayoutProduct? layout) ||
            layout is null)
        {
            AddError(HudErrorCode.InventoryCapacityExceeded, item.Stamp, _product.Contexts.Inventory.Root, 0, snapshot?.Capacity ?? -1);
            return;
        }

        if (!AcceptContextAuthority(ref _inventory, item, _product.Contexts.Inventory.Root))
        {
            return;
        }

        _inventory.Set(item.Stamp, item, snapshot);
        for (int index = 0; index < _inventoryCooldowns.Length; index++)
        {
            ref InventoryCooldownState cooldown = ref _inventoryCooldowns[index];
            cooldown.HasAuthority = index < snapshot.Capacity;
            cooldown.Stamp = item.Stamp;
            cooldown.LastEvent = item;
            cooldown.Value = index < snapshot.Capacity ? snapshot.Cooldowns[index] : null;
            cooldown.ReceivedAt = _lastNow;
        }

        UpdateInventoryViews();
        EmitContext(HudChangeKind.Inventory, _product.Contexts.Inventory.Root, snapshot.Capacity, _inventoryOpen, item.Stamp);
    }

    private void ApplyInventoryCooldown(in HudEvent item)
    {
        int capacity = _inventory.Value?.Capacity ?? 0;
        if ((uint)item.Slot >= (uint)capacity || item.ContentId.IsEmpty ||
            (item.Kind == HudEventKind.InventoryCooldownStarted &&
                (item.Value <= 0 || item.Auxiliary < item.Value)))
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, item.ContentId, 0, item.Slot);
            return;
        }

        ref InventoryCooldownState state = ref _inventoryCooldowns[item.Slot];
        if (item.Kind == HudEventKind.InventoryCooldownFinished &&
            state.Value is { } active && active.SpellId != item.ContentId)
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, item.ContentId, 0, item.Slot);
            return;
        }

        if (!AcceptAuthority(state.HasAuthority, state.Stamp, state.LastEvent, item, item.ContentId, 0, item.Slot))
        {
            return;
        }

        state.HasAuthority = true;
        state.Stamp = item.Stamp;
        state.LastEvent = item;
        state.Value = item.Kind == HudEventKind.InventoryCooldownStarted ?
            new HudInventoryCooldown(item.ContentId, item.Value, item.Auxiliary) : null;
        state.ReceivedAt = _lastNow;
        UpdateInventoryViews();
        EmitContext(HudChangeKind.Inventory, _product.Contexts.Inventory.Root, item.Slot, _inventoryOpen, item.Stamp);
    }

    private void ApplyLoot(in HudEvent item)
    {
        HudLootSnapshot? snapshot = item.Loot;
        if (snapshot is null || snapshot.Items.Length > _product.Contexts.Loot.MaxEntries)
        {
            AddError(HudErrorCode.LootCapacityExceeded, item.Stamp, _product.Contexts.Loot.Root,
                snapshot?.CorpseEntityId ?? 0, snapshot?.Items.Length ?? -1);
            return;
        }

        if (!AcceptContextAuthority(ref _loot, item, _product.Contexts.Loot.Root))
        {
            return;
        }

        if (_loot.Value?.CorpseEntityId != snapshot.CorpseEntityId || !snapshot.Open)
        {
            _lootPage = 0;
        }

        _loot.Set(item.Stamp, item, snapshot);
        SetContextOrder(HudContextWindow.Loot, snapshot.Open);
        ClampLootPage();
        UpdateLootViews();
        ReconcileContextFocus();
        EmitContext(HudChangeKind.Loot, _product.Contexts.Loot.Root, _lootPage, snapshot.Open, item.Stamp);
    }

    private void ApplyQuestLog(in HudEvent item)
    {
        HudQuestLogSnapshot? snapshot = item.QuestLog;
        if (snapshot is null || snapshot.Quests.Length > _product.Contexts.QuestLog.MaxEntries ||
            snapshot.SecretComponents.Length > HudProduct.QuestLogSecretComponentCount ||
            snapshot.Quests.ToArray().Any(quest =>
                quest.Objectives.Length > HudProduct.QuestLogObjectiveCount ||
                quest.Reward.MandatoryItems.Length > HudProduct.QuestInfoRewardItemCount ||
                quest.Reward.AlternativeItems.Length > HudProduct.QuestInfoRewardItemCount ||
                quest.Reward.Reputations.Length > HudProduct.QuestInfoReputationCount ||
                quest.Reward.Currencies.Length > HudProduct.QuestInfoCurrencyCount))
        {
            AddError(HudErrorCode.QuestLogCapacityExceeded, item.Stamp, _product.Contexts.QuestLog.Root, 0,
                snapshot?.Quests.Length ?? -1);
            return;
        }

        if (!AcceptContextAuthority(ref _questLog, item, _product.Contexts.QuestLog.Root))
        {
            return;
        }

        bool hadAuthority = _questLog.HasAuthority;
        _questLog.Set(item.Stamp, item, snapshot);
        if (!hadAuthority)
        {
            _selectedQuestBookmark = snapshot.ActiveBookmark;
        }
        ReconcileShareInvitation(snapshot.ShareInvitation, item.Stamp);
        _shareOfferExpiresAt = snapshot.ShareOffer is { } offer ?
            DeadlineAfter(offer.RemainingMilliseconds) : 0;
        if (!_selectedQuestId.IsEmpty && !ContainsQuest(snapshot, _selectedQuestId))
        {
            _selectedQuestId = HudId.Empty;
        }

        UpdateQuestLogViews();
        EmitContext(HudChangeKind.QuestLog, _product.Contexts.QuestLog.Root, snapshot.Quests.Length, _questLogOpen, item.Stamp);
    }

    private void ApplyQuestInfo(in HudEvent item)
    {
        HudQuestInfoSnapshot? snapshot = item.QuestInfo;
        int dynamicEntries = snapshot is null ? -1 :
            snapshot.Reward.DynamicEntryCount + (snapshot.Quest?.Objectives.Length ?? 0);
        if (snapshot is null || dynamicEntries > _product.Contexts.QuestInfo.MaxDynamicEntries ||
            (snapshot.Quest?.Objectives.Length ?? 0) > HudProduct.QuestInfoObjectiveCount ||
            snapshot.Reward.MandatoryItems.Length > HudProduct.QuestInfoRewardItemCount ||
            snapshot.Reward.AlternativeItems.Length > HudProduct.QuestInfoRewardItemCount ||
            snapshot.Reward.Reputations.Length > HudProduct.QuestInfoReputationCount ||
            snapshot.Reward.Currencies.Length > HudProduct.QuestInfoCurrencyCount)
        {
            AddError(HudErrorCode.QuestInfoCapacityExceeded, item.Stamp, _product.Contexts.QuestInfo.InteractionRoot,
                snapshot?.NpcEntityId ?? 0, dynamicEntries);
            return;
        }

        if (!AcceptContextAuthority(ref _questInfo, item, _product.Contexts.QuestInfo.InteractionRoot))
        {
            return;
        }

        _questInfo.Set(item.Stamp, item, snapshot);
        _selectedRewardIndex = -1;
        _questInfoOpen = snapshot.Mode != HudQuestInfoMode.None;
        SetContextOrder(HudContextWindow.QuestInfo, _questInfoOpen);
        UpdateQuestInfoView();
        ReconcileContextFocus();
        EmitContext(HudChangeKind.QuestInfo, _product.Contexts.QuestInfo.InteractionRoot, (int)snapshot.Mode,
            snapshot.Mode != HudQuestInfoMode.None, item.Stamp);
    }

    private void ApplyCharacter(in HudEvent item)
    {
        HudCharacterSnapshot? snapshot = item.Character;
        if (snapshot is null)
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, _product.Contexts.Character.Root, 0, -1);
            return;
        }

        if (!AcceptContextAuthority(ref _character, item, _product.Contexts.Character.Root))
        {
            return;
        }

        _character.Set(item.Stamp, item, snapshot);
        UpdateCharacterViews();
        EmitContext(HudChangeKind.Character, _product.Contexts.Character.Root, snapshot.Level, _characterOpen, item.Stamp);
    }

    private void ApplyMessageBox(in HudEvent item)
    {
        if (item.EventId.IsEmpty ||
            (item.Kind == HudEventKind.MessageBoxOffered && item.MessageBox is not { IsValid: true }))
        {
            AddError(HudErrorCode.InvalidEvent, item.Stamp, item.EventId, 0, -1);
            return;
        }

        int index = FindMessageBox(item.EventId);
        if (item.Kind == HudEventKind.MessageBoxWithdrawn)
        {
            if (index >= 0 && item.Stamp.CompareTo(_messageBoxes[index].Stamp) >= 0)
            {
                RemoveMessageBox(index, false);
            }

            return;
        }

        OfferMessageBox(item.MessageBox!.Value, item.Stamp);
    }

    private bool AcceptContextAuthority<T>(ref ContextState<T> state, in HudEvent item, HudId related)
        where T : class
    {
        return AcceptAuthority(state.HasAuthority, state.Stamp, state.LastEvent, item, related, item.EntityId, -1);
    }

    private void EmitContext(HudChangeKind kind, HudId root, int value, bool visible, HudStamp revision) =>
        _diff.AddChange(new HudChange(kind, root, 0, visible, value, false, HudId.Empty, default, 0, revision));

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
                CancelTopContext();
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
            case HudInputKind.ToggleInventory:
                SetInventoryOpen(!_inventoryOpen);
                break;
            case HudInputKind.CloseInventory:
                SetInventoryOpen(false);
                break;
            case HudInputKind.MoveInventoryItem:
                MoveInventoryItem(input.Slot, input.Auxiliary, input.Flag);
                break;
            case HudInputKind.DropInventoryItem:
                InventorySlotCommand(HudCommand.DropInventoryItem(input.Slot, input.Value, _inventory.Stamp), HudCommandFamilies.DropInventoryItem, input.Slot);
                break;
            case HudInputKind.UseInventoryItem:
                InventorySlotCommand(HudCommand.UseInventoryItem(input.Slot, _inventory.Stamp), HudCommandFamilies.UseInventoryItem, input.Slot);
                break;
            case HudInputKind.DressInventoryItem:
                InventorySlotCommand(HudCommand.DressInventoryItem(input.Slot, _inventory.Stamp), HudCommandFamilies.DressInventoryItem, input.Slot);
                break;
            case HudInputKind.UndressInventoryItem:
                if ((uint)input.Slot < HudProduct.CharacterEquipmentSlotCount &&
                    _character.Value is { } character && character.Equipment[input.Slot] is not null)
                {
                    SendCommand(HudCommand.UndressInventoryItem(input.Slot, _character.Stamp), HudCommandFamilies.UndressInventoryItem);
                }

                break;
            case HudInputKind.TakeLootItem:
                TakeLootItem(input.Slot);
                break;
            case HudInputKind.TakeLootMoney:
                TakeLootMoney(input.Amount);
                break;
            case HudInputKind.TakeAllLoot:
                TakeAllLoot();
                break;
            case HudInputKind.LootPreviousPage:
                SetLootPage(_lootPage - 1);
                break;
            case HudInputKind.LootNextPage:
                SetLootPage(_lootPage + 1);
                break;
            case HudInputKind.CloseLoot:
                CloseLoot();
                break;
            case HudInputKind.ToggleQuestLog:
                SetQuestLogOpen(!_questLogOpen);
                break;
            case HudInputKind.CloseQuestLog:
                SetQuestLogOpen(false);
                break;
            case HudInputKind.SelectQuest:
                SelectQuest(input.Target);
                break;
            case HudInputKind.SelectQuestBookmark:
                SelectQuestBookmark((HudQuestLogBookmark)input.Value);
                break;
            case HudInputKind.EnterQuestFolder:
                SetQuestFolder(input.Target);
                break;
            case HudInputKind.LeaveQuestFolder:
                SetQuestFolder(HudId.Empty);
                break;
            case HudInputKind.AbandonQuest:
                AbandonQuest(input.Target);
                break;
            case HudInputKind.ConfirmAbandonQuest:
                ConfirmAbandonQuest(input.Target);
                break;
            case HudInputKind.DeclineAbandonQuest:
                ClearAbandonConfirmation();
                break;
            case HudInputKind.ShareQuest:
                ShareQuest(input.Target);
                break;
            case HudInputKind.AcceptSharedQuest:
                ResolveSharedQuest(input.Target, input.SecondaryTarget, accept: true);
                break;
            case HudInputKind.DeclineSharedQuest:
                ResolveSharedQuest(input.Target, input.SecondaryTarget, accept: false);
                break;
            case HudInputKind.ResolveMessageBox:
                ResolveMessageBox(input.Target, (HudMessageBoxDecision)input.Value);
                break;
            case HudInputKind.SelectTalkOption:
                SelectTalkOption(input.Slot);
                break;
            case HudInputKind.SelectQuestReward:
                SelectQuestReward(input.Slot);
                break;
            case HudInputKind.AcceptQuest:
                AcceptQuest();
                break;
            case HudInputKind.TurnInQuest:
                TurnInQuest();
                break;
            case HudInputKind.CloseQuestInfo:
                CloseQuestInfo();
                break;
            case HudInputKind.ToggleCharacter:
                SetCharacterOpen(!_characterOpen);
                break;
            case HudInputKind.CloseCharacter:
                SetCharacterOpen(false);
                break;
        }
    }

    private void SetInventoryOpen(bool open)
    {
        if (_inventoryOpen == open)
        {
            return;
        }

        _inventoryOpen = open;
        SetContextOrder(HudContextWindow.Inventory, open);
        _inventoryRead.Open = open;
        ReconcileContextFocus();
        EmitContext(HudChangeKind.Inventory, _product.Contexts.Inventory.Root, _inventoryRead.Capacity, open, _inventory.Stamp);
    }

    private void MoveInventoryItem(int fromSlot, int toSlot, bool moveNoMore)
    {
        HudInventorySnapshot? snapshot = _inventory.Value;
        int capacity = snapshot?.Capacity ?? 0;
        if ((uint)fromSlot >= (uint)capacity || (uint)toSlot >= (uint)capacity || fromSlot == toSlot)
        {
            return;
        }

        if (snapshot is null || snapshot.Slots[fromSlot] is null)
        {
            return;
        }

        SendCommand(HudCommand.MoveInventoryItem(fromSlot, toSlot, moveNoMore, _inventory.Stamp), HudCommandFamilies.MoveInventoryItem);
    }

    private void InventorySlotCommand(in HudCommand command, HudCommandFamilies family, int slot)
    {
        HudInventorySnapshot? snapshot = _inventory.Value;
        int capacity = snapshot?.Capacity ?? 0;
        if ((uint)slot >= (uint)capacity || snapshot is null || snapshot.Slots[slot] is not { } item ||
            (command.Kind == HudCommandKind.DropInventoryItem && command.Count > item.Count) ||
            (command.Kind == HudCommandKind.UseInventoryItem &&
                RemainingInventoryCooldown(_inventoryCooldowns[slot], _lastNow) > 0))
        {
            return;
        }

        SendCommand(command, family);
    }

    private void TakeLootItem(int entry)
    {
        HudLootSnapshot? snapshot = _loot.Value;
        if (snapshot is null || !snapshot.Open || (uint)entry >= (uint)snapshot.Items.Length)
        {
            return;
        }

        SendCommand(HudCommand.TakeLootItem(snapshot.CorpseEntityId, entry, _loot.Stamp), HudCommandFamilies.TakeLoot);
    }

    private void TakeLootMoney(long amount)
    {
        HudLootSnapshot? snapshot = _loot.Value;
        if (snapshot is { Open: true, Money: > 0 } && (amount == -1 || amount <= snapshot.Money))
        {
            SendCommand(HudCommand.TakeLootMoney(snapshot.CorpseEntityId, amount, _loot.Stamp), HudCommandFamilies.TakeLoot);
        }
    }

    private void TakeAllLoot()
    {
        HudLootSnapshot? snapshot = _loot.Value;
        if (snapshot is { Open: true } && (snapshot.Money > 0 || snapshot.Items.Length > 0))
        {
            SendCommand(HudCommand.TakeAllLoot(snapshot.CorpseEntityId, _loot.Stamp), HudCommandFamilies.TakeLoot);
        }
    }

    private void CloseLoot()
    {
        HudLootSnapshot? snapshot = _loot.Value;
        if (snapshot is null || !snapshot.Open)
        {
            return;
        }

        SendCommand(HudCommand.CloseLoot(snapshot.CorpseEntityId, _loot.Stamp), HudCommandFamilies.CloseLoot);
    }

    private void SetLootPage(int page)
    {
        int pageCount = LootPageCount();
        int next = Math.Clamp(page, 0, Math.Max(0, pageCount - 1));
        if (next == _lootPage)
        {
            return;
        }

        _lootPage = next;
        UpdateLootViews();
        EmitContext(HudChangeKind.Loot, _product.Contexts.Loot.Root, _lootPage, _loot.Value?.Open ?? false, _loot.Stamp);
    }

    private void SetQuestLogOpen(bool open)
    {
        if (_questLogOpen == open)
        {
            return;
        }

        _questLogOpen = open;
        SetContextOrder(HudContextWindow.QuestLog, open);
        _questLogRead.Open = open;
        ReconcileContextFocus();
        EmitContext(HudChangeKind.QuestLog, _product.Contexts.QuestLog.Root, _questLogRead.Count, open, _questLog.Stamp);
    }

    private void SelectQuest(HudId questId)
    {
        if (questId.IsEmpty || _questLog.Value is not { } snapshot || !ContainsQuest(snapshot, questId) ||
            _selectedQuestId == questId)
        {
            return;
        }

        _selectedQuestId = questId;
        UpdateQuestLogViews();
        EmitContext(HudChangeKind.QuestLog, _product.Contexts.QuestLog.Root, _questLogRead.Count, _questLogOpen, _questLog.Stamp);
    }

    private void SelectQuestBookmark(HudQuestLogBookmark bookmark)
    {
        if (_selectedQuestBookmark == bookmark)
        {
            return;
        }

        _selectedQuestBookmark = bookmark;
        _selectedQuestFolderId = HudId.Empty;
        UpdateQuestLogViews();
        EmitContext(HudChangeKind.QuestLog, _product.Contexts.QuestLog.Root, _questLogRead.Count, _questLogOpen, _questLog.Stamp);
    }

    private void SetQuestFolder(HudId folderId)
    {
        if (_selectedQuestFolderId == folderId)
        {
            return;
        }

        _selectedQuestFolderId = folderId;
        UpdateQuestLogViews();
        EmitContext(HudChangeKind.QuestLog, _product.Contexts.QuestLog.Root, _questLogRead.Count, _questLogOpen, _questLog.Stamp);
    }

    private void AbandonQuest(HudId questId)
    {
        HudId target = questId.IsEmpty ? _selectedQuestId : questId;
        if (target.IsEmpty || _questLog.Value is not { } snapshot)
        {
            return;
        }

        for (int index = 0; index < snapshot.Quests.Length; index++)
        {
            HudQuestDocument quest = snapshot.Quests[index];
            if (quest.QuestId == target && quest.CanAbandon)
            {
                _pendingAbandonQuestId = target;
                HudId requestId = AbandonRequestId(target);
                var request = new HudMessageBoxRequest(
                    requestId,
                    HudMessageBoxPurpose.QuestAbandon,
                    QuestAbandonHeaderId,
                    QuestAbandonBodyId,
                    target,
                    HudId.Empty,
                    HudMessageBoxButtons.AcceptDecline,
                    HudMessageBoxDecision.Decline,
                    30_000,
                    0,
                    _questLog.Stamp);
                if (!OfferLocalMessageBox(request))
                {
                    _pendingAbandonQuestId = HudId.Empty;
                    return;
                }

                _abandonConfirmationExpiresAt = FindMessageBoxExpiry(requestId);
                UpdateQuestLogViews();
                EmitContext(HudChangeKind.QuestLog, _product.Contexts.QuestLog.Root, _questLogRead.Count, _questLogOpen, _questLog.Stamp);
                return;
            }
        }
    }

    private void ConfirmAbandonQuest(HudId questId)
    {
        if (!_pendingAbandonQuestId.IsEmpty && questId == _pendingAbandonQuestId &&
            _lastNow <= _abandonConfirmationExpiresAt)
        {
            ResolveMessageBox(AbandonRequestId(questId), HudMessageBoxDecision.Accept);
            return;
        }

        ClearAbandonConfirmation();
    }

    private void ClearAbandonConfirmation()
    {
        if (_pendingAbandonQuestId.IsEmpty)
        {
            return;
        }

        _pendingAbandonQuestId = HudId.Empty;
        _abandonConfirmationExpiresAt = 0;
        RemoveMessageBox(FindMessageBoxByPurpose(HudMessageBoxPurpose.QuestAbandon), false);
        UpdateQuestLogViews();
        EmitContext(HudChangeKind.QuestLog, _product.Contexts.QuestLog.Root, _questLogRead.Count, _questLogOpen, _questLog.Stamp);
    }

    private long DeadlineAfter(int remainingMilliseconds) =>
        _lastNow > long.MaxValue - remainingMilliseconds ? long.MaxValue : _lastNow + remainingMilliseconds;

    private void ShareQuest(HudId questId)
    {
        if (!questId.IsEmpty && _questLog.Value is { } snapshot && ContainsQuest(snapshot, questId))
        {
            SendCommand(HudCommand.ShareQuest(questId, _questLog.Stamp), HudCommandFamilies.ShareQuest);
        }
    }

    private void ResolveSharedQuest(HudId shareId, HudId questId, bool accept)
    {
        if (_questLog.Value?.ShareInvitation is not { } invitation ||
            invitation.ShareId != shareId || invitation.QuestId != questId || _lastNow > _shareInvitationExpiresAt)
        {
            return;
        }

        ResolveMessageBox(shareId, accept ? HudMessageBoxDecision.Accept : HudMessageBoxDecision.Decline);
    }

    private void ReconcileShareInvitation(HudQuestShareInvitation? invitation, HudStamp stamp)
    {
        int existing = FindMessageBoxByPurpose(HudMessageBoxPurpose.QuestShareInvitation);
        if (invitation is null)
        {
            if (existing >= 0)
            {
                RemoveMessageBox(existing, false);
            }

            _shareInvitationExpiresAt = 0;
            return;
        }

        var request = new HudMessageBoxRequest(
            invitation.Value.ShareId,
            HudMessageBoxPurpose.QuestShareInvitation,
            QuestShareHeaderId,
            QuestShareBodyId,
            invitation.Value.QuestId,
            invitation.Value.SharerNameId,
            HudMessageBoxButtons.AcceptDecline,
            HudMessageBoxDecision.Decline,
            30_000,
            invitation.Value.OfferRemainingMilliseconds,
            stamp);
        if (existing >= 0 && _messageBoxes[existing].Request.RequestId != request.RequestId)
        {
            RemoveMessageBox(existing, false);
        }

        OfferMessageBox(request, stamp);
        _shareInvitationExpiresAt = FindMessageBoxExpiry(request.RequestId);
    }

    private bool OfferLocalMessageBox(in HudMessageBoxRequest request) => OfferMessageBox(request, default);

    private bool OfferMessageBox(in HudMessageBoxRequest request, HudStamp stamp)
    {
        int index = FindMessageBox(request.RequestId);
        if (index < 0)
        {
            index = FindFreeMessageBox();
            if (index < 0)
            {
                AddError(HudErrorCode.MessageBoxCapacityExceeded, stamp, request.RequestId, 0, -1);
                return false;
            }
        }
        else if (stamp != default && stamp.CompareTo(_messageBoxes[index].Stamp) < 0)
        {
            return false;
        }

        long order = _messageBoxes[index].Occupied ? _messageBoxes[index].Order : ++_messageBoxSequence;
        _messageBoxes[index] = new HudMessageBoxState
        {
            Occupied = true,
            Request = request,
            Stamp = stamp,
            ExpiresAt = DeadlineAfter(request.EffectiveLifetimeMilliseconds),
            Order = order,
        };
        UpdateMessageBoxViews();
        SetContextOrder(HudContextWindow.MessageBox, true);
        ReconcileContextFocus();
        EmitContext(HudChangeKind.MessageBox, _product.Contexts.MessageBox.Root,
            _messageBoxRead.Count, true, stamp);
        return true;
    }

    private void AdvanceMessageBoxes(long now)
    {
        bool remainingChanged = false;
        for (int index = 0; index < _messageBoxes.Length; index++)
        {
            if (!_messageBoxes[index].Occupied)
            {
                continue;
            }

            if (now >= _messageBoxes[index].ExpiresAt)
            {
                HudId requestId = _messageBoxes[index].Request.RequestId;
                HudMessageBoxDecision decision = _messageBoxes[index].Request.DefaultDecision;
                ResolveMessageBox(requestId, decision);
                index = -1;
                continue;
            }

            remainingChanged = true;
        }

        if (remainingChanged)
        {
            UpdateMessageBoxViews();
        }
    }

    private void ResolveMessageBox(HudId requestId, HudMessageBoxDecision decision)
    {
        int index = FindMessageBox(requestId);
        if (index < 0 || (uint)decision > (uint)HudMessageBoxDecision.Decline)
        {
            return;
        }

        HudMessageBoxRequest request = _messageBoxes[index].Request;
        switch (request.Purpose)
        {
            case HudMessageBoxPurpose.QuestAbandon:
                if (decision == HudMessageBoxDecision.Accept)
                {
                    SendCommand(HudCommand.AbandonQuest(request.RelatedId, request.ExpectedRevision),
                        HudCommandFamilies.AbandonQuest);
                }

                _pendingAbandonQuestId = HudId.Empty;
                _abandonConfirmationExpiresAt = 0;
                break;
            case HudMessageBoxPurpose.QuestShareInvitation:
                SendCommand(
                    decision == HudMessageBoxDecision.Accept
                        ? HudCommand.AcceptSharedQuest(request.RequestId, request.RelatedId, request.ExpectedRevision)
                        : HudCommand.DeclineSharedQuest(request.RequestId, request.RelatedId, request.ExpectedRevision),
                    decision == HudMessageBoxDecision.Accept
                        ? HudCommandFamilies.AcceptSharedQuest
                        : HudCommandFamilies.DeclineSharedQuest);
                _shareInvitationExpiresAt = 0;
                break;
            case HudMessageBoxPurpose.ItemConfirmation:
            case HudMessageBoxPurpose.TradeInvitation:
                SendCommand(HudCommand.ResolveMessageBox(
                    request.RequestId, request.Purpose, decision, request.RelatedId,
                    request.SecondaryId, request.ExpectedRevision), HudCommandFamilies.ResolveMessageBox);
                break;
        }

        RemoveMessageBox(index, false);
        UpdateQuestLogViews();
    }

    private void RemoveMessageBox(int index, bool resolveDefault)
    {
        if ((uint)index >= (uint)_messageBoxes.Length || !_messageBoxes[index].Occupied)
        {
            return;
        }

        if (resolveDefault)
        {
            ResolveMessageBox(_messageBoxes[index].Request.RequestId, _messageBoxes[index].Request.DefaultDecision);
            return;
        }

        HudStamp stamp = _messageBoxes[index].Stamp;
        _messageBoxes[index] = default;
        UpdateMessageBoxViews();
        bool open = _messageBoxRead.Count > 0;
        SetContextOrder(HudContextWindow.MessageBox, open);
        ReconcileContextFocus();
        EmitContext(HudChangeKind.MessageBox, _product.Contexts.MessageBox.Root,
            _messageBoxRead.Count, open, stamp);
    }

    private int FindMessageBox(HudId requestId)
    {
        for (int index = 0; index < _messageBoxes.Length; index++)
        {
            if (_messageBoxes[index].Occupied && _messageBoxes[index].Request.RequestId == requestId)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindMessageBoxByPurpose(HudMessageBoxPurpose purpose)
    {
        for (int index = 0; index < _messageBoxes.Length; index++)
        {
            if (_messageBoxes[index].Occupied && _messageBoxes[index].Request.Purpose == purpose)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindFreeMessageBox()
    {
        for (int index = 0; index < _messageBoxes.Length; index++)
        {
            if (!_messageBoxes[index].Occupied)
            {
                return index;
            }
        }

        return -1;
    }

    private long FindMessageBoxExpiry(HudId requestId)
    {
        int index = FindMessageBox(requestId);
        return index < 0 ? 0 : _messageBoxes[index].ExpiresAt;
    }

    private static HudId AbandonRequestId(HudId questId) => new($"message.quest-abandon.{questId.Value}");

    private void SelectTalkOption(int option)
    {
        HudQuestInfoSnapshot? snapshot = _questInfo.Value;
        if (_questInfoOpen && snapshot is not null && (uint)option < (uint)snapshot.TalkOptions.Length)
        {
            _selectedRewardIndex = -1;
            snapshot = snapshot.WithSelectedTalkOption(option);
            _questInfo.Value = snapshot;
            UpdateQuestInfoView();
            EmitContext(HudChangeKind.QuestInfo, _product.Contexts.QuestInfo.InteractionRoot, (int)snapshot.Mode, true, _questInfo.Stamp);
        }
    }

    private void SelectQuestReward(int rewardIndex)
    {
        HudQuestInfoSnapshot? snapshot = _questInfo.Value;
        if (_questInfoOpen && snapshot?.Mode == HudQuestInfoMode.TurnIn &&
            (uint)rewardIndex < (uint)snapshot.Reward.AlternativeItems.Length)
        {
            _selectedRewardIndex = rewardIndex;
            UpdateQuestInfoView();
            EmitContext(HudChangeKind.QuestInfo, _product.Contexts.QuestInfo.InteractionRoot, (int)snapshot.Mode, true, _questInfo.Stamp);
        }
    }

    private void AcceptQuest()
    {
        HudQuestInfoSnapshot? snapshot = _questInfo.Value;
        if (_questInfoOpen && snapshot?.Mode == HudQuestInfoMode.Offer && snapshot.Quest is not null)
        {
            SendCommand(HudCommand.AcceptQuest(snapshot.Quest.QuestId, snapshot.NpcEntityId, _questInfo.Stamp), HudCommandFamilies.AcceptQuest);
        }
    }

    private void TurnInQuest()
    {
        HudQuestInfoSnapshot? snapshot = _questInfo.Value;
        if (_questInfoOpen && snapshot?.Mode == HudQuestInfoMode.TurnIn && snapshot.Quest is not null)
        {
            int rewardIndex = snapshot.Reward.AlternativeItems.Length == 0 ? -1 : _selectedRewardIndex;
            if (snapshot.Reward.AlternativeItems.Length == 0 || rewardIndex >= 0)
            {
                SendCommand(HudCommand.TurnInQuest(
                    snapshot.Quest.QuestId, snapshot.NpcEntityId, rewardIndex, _questInfo.Stamp),
                    HudCommandFamilies.TurnInQuest);
            }
        }
    }

    private void CloseQuestInfo()
    {
        if (!_questInfoOpen)
        {
            return;
        }

        _questInfoOpen = false;
        _selectedRewardIndex = -1;
        SetContextOrder(HudContextWindow.QuestInfo, false);
        UpdateQuestInfoView();
        ReconcileContextFocus();
        EmitContext(HudChangeKind.QuestInfo, _product.Contexts.QuestInfo.InteractionRoot, 0, false, _questInfo.Stamp);
    }

    private void SetCharacterOpen(bool open)
    {
        if (_characterOpen == open)
        {
            return;
        }

        _characterOpen = open;
        SetContextOrder(HudContextWindow.Character, open);
        _characterRead.Open = open;
        ReconcileContextFocus();
        EmitContext(HudChangeKind.Character, _product.Contexts.Character.Root, _characterRead.Level, open, _character.Stamp);
    }

    private void CancelTopContext()
    {
        if (_openContextCount == 0)
        {
            SetFocus(HudFocus.World);
            return;
        }

        switch (_openContextOrder[_openContextCount - 1])
        {
            case HudContextWindow.Inventory:
                SetInventoryOpen(false);
                break;
            case HudContextWindow.Loot:
                CloseLoot();
                break;
            case HudContextWindow.QuestLog:
                SetQuestLogOpen(false);
                break;
            case HudContextWindow.QuestInfo:
                CloseQuestInfo();
                break;
            case HudContextWindow.Character:
                SetCharacterOpen(false);
                break;
            case HudContextWindow.MessageBox:
                int messageIndex = FindMessageBox(_messageBoxRead.ActiveRequestId);
                if (messageIndex >= 0)
                {
                    ResolveMessageBox(
                        _messageBoxes[messageIndex].Request.RequestId,
                        _messageBoxes[messageIndex].Request.DefaultDecision);
                }
                break;
        }
    }

    private void ReconcileContextFocus()
    {
        if (_openContextCount > 0 &&
            _openContextOrder[_openContextCount - 1] is HudContextWindow.QuestInfo or HudContextWindow.MessageBox)
        {
            SetFocus(HudFocus.Modal);
        }
        else if (_openContextCount > 0)
        {
            SetFocus(HudFocus.Hud);
        }
        else if (_focus is HudFocus.Hud or HudFocus.Modal)
        {
            SetFocus(HudFocus.World);
        }
    }

    private void SetContextOrder(HudContextWindow context, bool open)
    {
        int found = -1;
        for (int index = 0; index < _openContextCount; index++)
        {
            if (_openContextOrder[index] == context)
            {
                found = index;
                break;
            }
        }

        if (found >= 0)
        {
            for (int index = found; index < _openContextCount - 1; index++)
            {
                _openContextOrder[index] = _openContextOrder[index + 1];
            }

            _openContextCount--;
        }

        if (open)
        {
            _openContextOrder[_openContextCount++] = context;
        }
    }

    private void ActivateAction(int slot)
    {
        if ((uint)slot >= (uint)_actions.Length)
        {
            return;
        }

        ref ActionState state = ref _actions[slot];
        if (state.AbilityId.IsEmpty || !state.Enabled || RemainingActionCooldown(state, _lastNow) > 0)
        {
            return;
        }

        SendCommand(HudCommand.ActivateAction(slot, state.Stamp), HudCommandFamilies.ActivateAction);
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

        _diff.AddChange(new HudChange(
            HudChangeKind.TargetSelection,
            HudId.Empty,
            0,
            _selectedTargetEntityId != 0,
            0,
            _selectedTargetHasAuthority,
            HudId.Empty,
            default,
            Revision: _selectedTargetStamp));

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
            _actionViews[index].CooldownMilliseconds,
            state.Enabled,
            state.AbilityId,
            default,
            state.CooldownDurationMilliseconds,
            state.Stamp));
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
            RemainingActionCooldown(state, _lastNow),
            state.CooldownDurationMilliseconds,
            state.Enabled,
            state.Stamp,
            state.HasAuthority);
    }

    private void AdvanceActionCooldowns(long now)
    {
        for (int index = 0; index < _actions.Length; index++)
        {
            int remaining = RemainingActionCooldown(_actions[index], now);
            if (_actionViews[index].CooldownMilliseconds != remaining)
            {
                UpdateActionView(index);
                EmitAction(index);
            }
        }
    }

    private static int RemainingActionCooldown(in ActionState state, long now)
    {
        if (state.CooldownMilliseconds <= 0)
        {
            return 0;
        }

        long elapsed = Math.Max(0, now - state.CooldownReceivedAt);
        return (int)Math.Max(0, state.CooldownMilliseconds - elapsed);
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

    private void UpdateInventoryViews()
    {
        HudInventorySnapshot? snapshot = _inventory.Value;
        HudInventoryLayoutProduct? layout = null;
        if (snapshot is not null)
        {
            _product.Contexts.Inventory.TryFindLayout(snapshot.Capacity, out layout);
        }

        for (int index = 0; index < _inventorySlotViews.Length; index++)
        {
            if (snapshot is not null && layout is not null && index < snapshot.Capacity)
            {
                HudItemStack? stack = snapshot.Slots[index];
                HudInventoryCooldown? cooldown = _inventoryCooldowns[index].Value;
                int remaining = RemainingInventoryCooldown(_inventoryCooldowns[index], _lastNow);
                int displayCount = stack is { Count: > 1 } ? stack.Value.Count :
                    stack is { CounterValue: > 1 } ? stack.Value.CounterValue : 0;
                _inventorySlotViews[index] = new HudInventorySlotView(
                    layout.Slots[index], index, stack?.ItemId ?? HudId.Empty, stack?.InstanceId ?? 0,
                    stack?.Count ?? 0, stack?.CounterValue ?? 0, displayCount,
                    stack?.Bound ?? false, stack?.Cursed ?? false,
                    stack?.IsQuestOperator ?? false, stack?.RemoveTime ?? 0,
                    stack?.RuneId ?? HudId.Empty, stack?.RuneSlotId ?? HudId.Empty,
                    cooldown?.SpellId ?? HudId.Empty, remaining, cooldown?.DurationMilliseconds ?? 0,
                    stack?.IsQuestOperator ?? false,
                    stack is not null, true);
            }
            else
            {
                _inventorySlotViews[index] = new HudInventorySlotView(
                    HudId.Empty, index, HudId.Empty, 0, 0, 0, 0, false, false, false, 0,
                    HudId.Empty, HudId.Empty, HudId.Empty, 0, 0, false, false, false);
            }
        }

        for (int index = 0; index < _inventoryPartitionViews.Length; index++)
        {
            if (snapshot is not null && layout is not null && index < layout.Partitions.Length)
            {
                HudInventoryPartitionProduct bag = layout.Partitions[index];
                _inventoryPartitionViews[index] = new HudInventoryPartitionView(
                    bag.Element, index, bag.FirstSlot, bag.SlotCount, true);
            }
            else
            {
                _inventoryPartitionViews[index] = new HudInventoryPartitionView(HudId.Empty, index, 0, 0, false);
            }
        }

        _inventoryRead.HasAuthority = _inventory.HasAuthority;
        _inventoryRead.Open = _inventoryOpen;
        _inventoryRead.Capacity = snapshot?.Capacity ?? 0;
        _inventoryRead.Currency = snapshot?.Currency ?? 0;
        _inventoryRead.EquippedBag = snapshot?.EquippedBag ?? default;
        _inventoryRead.LayoutElement = layout?.Element ?? HudId.Empty;
        _inventoryRead.Revision = _inventory.Stamp;
    }

    private void AdvanceInventoryCooldowns(long now)
    {
        int capacity = _inventory.Value?.Capacity ?? 0;
        bool changed = false;
        for (int index = 0; index < capacity; index++)
        {
            if (_inventorySlotViews[index].CooldownRemainingMilliseconds !=
                RemainingInventoryCooldown(_inventoryCooldowns[index], now))
            {
                changed = true;
                break;
            }
        }

        if (changed)
        {
            UpdateInventoryViews();
            EmitContext(HudChangeKind.Inventory, _product.Contexts.Inventory.Root, capacity, _inventoryOpen, _inventory.Stamp);
        }
    }

    private static int RemainingInventoryCooldown(in InventoryCooldownState state, long now)
    {
        if (state.Value is not { } cooldown)
        {
            return 0;
        }

        long elapsed = Math.Max(0, now - state.ReceivedAt);
        return (int)Math.Max(0, cooldown.RemainingMilliseconds - elapsed);
    }

    private void UpdateLootViews()
    {
        HudLootSnapshot? snapshot = _loot.Value;
        ReadOnlySpan<HudId> elements = _product.Contexts.Loot.PageSlots;
        int start = _lootPage * HudProduct.LootPageSize;
        for (int index = 0; index < _lootSlotViews.Length; index++)
        {
            int entry = start + index;
            if (snapshot is not null && entry < snapshot.Items.Length)
            {
                HudLootItem item = snapshot.Items[entry];
                _lootSlotViews[index] = new HudLootSlotView(
                    elements[index], index, entry, item.ItemId, item.Count, item.Cursed, true);
            }
            else
            {
                _lootSlotViews[index] = new HudLootSlotView(
                    elements[index], index, entry, HudId.Empty, 0, false, false);
            }
        }

        _lootRead.HasAuthority = _loot.HasAuthority;
        _lootRead.Open = snapshot?.Open ?? false;
        _lootRead.CorpseEntityId = snapshot?.CorpseEntityId ?? 0;
        _lootRead.Money = snapshot?.Money ?? 0;
        _lootRead.Refusal = snapshot?.Refusal ?? HudLootRefusal.None;
        _lootRead.Page = _lootPage;
        _lootRead.PageCount = LootPageCount();
        _lootRead.EntryCount = snapshot?.Items.Length ?? 0;
        _lootRead.Revision = _loot.Stamp;
    }

    private int LootPageCount()
    {
        int count = _loot.Value?.Items.Length ?? 0;
        return Math.Max(1, (count + HudProduct.LootPageSize - 1) / HudProduct.LootPageSize);
    }

    private void ClampLootPage() =>
        _lootPage = Math.Clamp(_lootPage, 0, Math.Max(0, LootPageCount() - 1));

    private void UpdateQuestLogViews()
    {
        HudQuestLogSnapshot? snapshot = _questLog.Value;
        int count = snapshot?.Quests.Length ?? 0;
        for (int index = 0; index < _questLogViews.Length; index++)
        {
            if (snapshot is not null && index < snapshot.Quests.Length)
            {
                HudQuestDocument quest = snapshot.Quests[index];
                _questLogViews[index] = new HudQuestLogEntryView(
                    _product.Contexts.QuestLog.Entries[index],
                    index,
                    quest.QuestId,
                    quest.TitleId,
                    quest.DescriptionId,
                    quest.State,
                    quest.CanAbandon,
                    true,
                    quest.QuestId == _selectedQuestId,
                    quest);
            }
            else
            {
                _questLogViews[index] = new HudQuestLogEntryView(
                    _product.Contexts.QuestLog.Entries[index], index, HudId.Empty, HudId.Empty,
                    HudId.Empty, default, false, false, false, null);
            }
        }

        _questLogRead.HasAuthority = _questLog.HasAuthority;
        _questLogRead.Open = _questLogOpen;
        _questLogRead.Count = count;
        _questLogRead.ActiveBookmark = _selectedQuestBookmark;
        _questLogRead.SelectedFolderId = _selectedQuestFolderId;
        _questLogRead.SecretComponentCount = snapshot?.SecretComponents.Length ?? 0;
        _questLogRead.SelectedQuestId = _selectedQuestId;
        _questLogRead.PendingAbandonQuestId = _pendingAbandonQuestId;
        _questLogRead.AbandonConfirmationExpiresAtMilliseconds = _abandonConfirmationExpiresAt;
        _questLogRead.ShareInvitation = _shareInvitationExpiresAt > 0 ? snapshot?.ShareInvitation : null;
        _questLogRead.ShareInvitationExpiresAtMilliseconds = _shareInvitationExpiresAt;
        _questLogRead.ShareOffer = _shareOfferExpiresAt > 0 ? snapshot?.ShareOffer : null;
        _questLogRead.ShareOfferExpiresAtMilliseconds = _shareOfferExpiresAt;
        _questLogRead.Revision = _questLog.Stamp;
    }

    private void UpdateQuestInfoView()
    {
        HudQuestInfoSnapshot? snapshot = _questInfo.Value;
        UpdateQuestTalkOptionViews();
        _diff.ReadModel.QuestInfo = new HudQuestInfoView(
            _product.Contexts.QuestInfo.InteractionRoot,
            _product.Contexts.QuestInfo.DetailRoot,
            _questInfo.HasAuthority,
            _questInfoOpen,
            snapshot?.Mode ?? HudQuestInfoMode.None,
            snapshot?.Quest?.QuestId ?? HudId.Empty,
            snapshot?.NpcEntityId ?? 0,
            snapshot?.Refusal ?? HudQuestRefusal.None,
            snapshot?.Quest,
            snapshot?.Reward,
            snapshot?.SelectedTalkOption ?? -1,
            _selectedRewardIndex,
            _questInfo.Stamp,
            _questTalkOptionsRead);
    }

    private void UpdateQuestTalkOptionViews()
    {
        HudQuestInfoSnapshot? snapshot = _questInfo.Value;
        ReadOnlySpan<HudId> elements = _product.Contexts.QuestInfo.TalkOptions;
        int count = snapshot?.TalkOptions.Length ?? 0;
        for (int index = 0; index < _questTalkOptionViews.Length; index++)
        {
            if (snapshot is not null && index < snapshot.TalkOptions.Length)
            {
                HudQuestTalkOption option = snapshot.TalkOptions[index];
                _questTalkOptionViews[index] = new HudQuestTalkOptionView(
                    elements[index], index, option.OptionId, option.LabelId, option.MarkId,
                    option.Quest?.State, true, index == snapshot.SelectedTalkOption, option.Quest);
            }
            else
            {
                _questTalkOptionViews[index] = new HudQuestTalkOptionView(
                    elements[index], index, HudId.Empty, HudId.Empty, HudId.Empty,
                    null, false, false, null);
            }
        }

        _questTalkOptionsRead.Count = count;
    }

    private void UpdateMessageBoxViews()
    {
        int count = 0;
        int activeIndex = -1;
        long activeOrder = long.MinValue;
        for (int index = 0; index < _messageBoxes.Length; index++)
        {
            if (!_messageBoxes[index].Occupied)
            {
                continue;
            }

            count++;
            if (_messageBoxes[index].Order > activeOrder)
            {
                activeOrder = _messageBoxes[index].Order;
                activeIndex = index;
            }
        }

        for (int index = 0; index < _messageBoxViews.Length; index++)
        {
            if (_messageBoxes[index].Occupied)
            {
                long remaining = Math.Max(0, _messageBoxes[index].ExpiresAt - _lastNow);
                _messageBoxViews[index] = new HudMessageBoxView(
                    _product.Contexts.MessageBox.Root,
                    index,
                    _messageBoxes[index].Request,
                    (int)Math.Min(int.MaxValue, remaining),
                    index == activeIndex,
                    index == activeIndex);
            }
            else
            {
                _messageBoxViews[index] = new HudMessageBoxView(
                    _product.Contexts.MessageBox.Root, index, default, 0, false, false);
            }
        }

        _messageBoxRead.Count = count;
        _messageBoxRead.ActiveRequestId = activeIndex < 0
            ? HudId.Empty
            : _messageBoxes[activeIndex].Request.RequestId;
    }

    private void UpdateCharacterViews()
    {
        HudCharacterSnapshot? snapshot = _character.Value;
        ReadOnlySpan<HudId> equipmentElements = _product.Contexts.Character.EquipmentSlots;
        for (int index = 0; index < _characterEquipmentViews.Length; index++)
        {
            HudItemStack? item = snapshot?.Equipment[index];
            _characterEquipmentViews[index] = new HudCharacterEquipmentView(
                equipmentElements[index], index, (HudCharacterEquipmentRole)index,
                item?.ItemId ?? HudId.Empty, item?.InstanceId ?? 0, item?.Count ?? 0,
                item?.Bound ?? false, item?.Cursed ?? false, item is not null);
        }

        ReadOnlySpan<HudId> statElements = _product.Contexts.Character.StatRows;
        for (int index = 0; index < _characterStatViews.Length; index++)
        {
            HudCharacterStat? stat = snapshot is null ? null : snapshot.Stats[index];
            _characterStatViews[index] = new HudCharacterStatView(
                statElements[index], index, stat?.StatId ?? HudId.Empty, stat?.BaseValue,
                stat?.EffectiveValue, stat?.LongTermValue, snapshot is not null);
        }

        HudItemStack? bag = snapshot?.Bag;
        _characterRead.HasAuthority = _character.HasAuthority;
        _characterRead.Open = _characterOpen;
        _characterRead.NameId = snapshot?.NameId ?? HudId.Empty;
        _characterRead.Level = snapshot?.Level ?? 0;
        _characterRead.Bag = new HudCharacterEquipmentView(
            _product.Contexts.Character.BagSlot,
            HudProduct.CharacterBagSlot,
            HudCharacterEquipmentRole.Bag,
            bag?.ItemId ?? HudId.Empty,
            bag?.InstanceId ?? 0,
            bag?.Count ?? 0,
            bag?.Bound ?? false,
            bag?.Cursed ?? false,
            bag is not null);
        _characterRead.Revision = _character.Stamp;
    }

    private static bool ContainsQuest(HudQuestLogSnapshot snapshot, HudId questId)
    {
        for (int index = 0; index < snapshot.Quests.Length; index++)
        {
            if (snapshot.Quests[index].QuestId == questId)
            {
                return true;
            }
        }

        return false;
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
        HudInputKind.MoveInventoryItem => input.Slot >= 0 && input.Auxiliary >= 0 && input.Slot != input.Auxiliary,
        HudInputKind.DropInventoryItem => input.Slot >= 0 && input.Value > 0,
        HudInputKind.UseInventoryItem or HudInputKind.DressInventoryItem or HudInputKind.UndressInventoryItem or
            HudInputKind.TakeLootItem or HudInputKind.SelectTalkOption or HudInputKind.SelectQuestReward => input.Slot >= 0,
        HudInputKind.SelectQuest or HudInputKind.AbandonQuest or HudInputKind.ConfirmAbandonQuest or
            HudInputKind.ShareQuest or HudInputKind.EnterQuestFolder => !input.Target.IsEmpty,
        HudInputKind.SelectQuestBookmark => (uint)input.Value < HudProduct.QuestLogBookmarkCount,
        HudInputKind.AcceptSharedQuest or HudInputKind.DeclineSharedQuest =>
            !input.Target.IsEmpty && !input.SecondaryTarget.IsEmpty,
        HudInputKind.ResolveMessageBox =>
            !input.Target.IsEmpty && (uint)input.Value <= (uint)HudMessageBoxDecision.Decline,
        HudInputKind.Cancel or >= HudInputKind.PointerMoved and <= HudInputKind.DragEnded or
            HudInputKind.ToggleInventory or HudInputKind.CloseInventory or
            HudInputKind.TakeAllLoot or HudInputKind.DeclineAbandonQuest or
            HudInputKind.LootPreviousPage or HudInputKind.LootNextPage or HudInputKind.CloseLoot or
            HudInputKind.ToggleQuestLog or HudInputKind.CloseQuestLog or HudInputKind.LeaveQuestFolder or
            HudInputKind.AcceptQuest or
            HudInputKind.TurnInQuest or HudInputKind.CloseQuestInfo or HudInputKind.ToggleCharacter or
            HudInputKind.CloseCharacter => true,
        HudInputKind.TakeLootMoney => input.Amount == -1 || input.Amount > 0,
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
        public int CooldownDurationMilliseconds;
        public long CooldownReceivedAt;
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

    private struct ContextState<T>
        where T : class
    {
        public bool HasAuthority;
        public HudStamp Stamp;
        public HudEvent LastEvent;
        public T? Value;

        public void Set(HudStamp stamp, in HudEvent lastEvent, T value)
        {
            HasAuthority = true;
            Stamp = stamp;
            LastEvent = lastEvent;
            Value = value;
        }
    }

    private struct InventoryCooldownState
    {
        public bool HasAuthority;
        public HudStamp Stamp;
        public HudEvent LastEvent;
        public HudInventoryCooldown? Value;
        public long ReceivedAt;
    }

    private enum HudContextWindow
    {
        Inventory,
        Loot,
        QuestLog,
        QuestInfo,
        Character,
        MessageBox,
    }
}
