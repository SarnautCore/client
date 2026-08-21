using System.Globalization;
using Sarnaut.Protocol.V1;
using SarnautCore.NativeHud;
using SarnautCore.Network;

namespace SarnautCore.Networking;

public sealed record SessionHudAdapterOptions(
    int ReliableEventCapacity = 512,
    int SnapshotEntityCapacity = 1024,
    int CommandCapacity = 64)
{
    internal void Validate()
    {
        if (ReliableEventCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ReliableEventCapacity));
        }

        if (SnapshotEntityCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SnapshotEntityCapacity));
        }

        if (CommandCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandCapacity));
        }
    }
}

public enum SessionHudObservation
{
    NotSubscribed,
    Observed,
    Projected,
    Terminal,
}

public enum SessionHudFaultCode
{
    None,
    ReliableEventQueueFull,
    SnapshotEntityCapacityExceeded,
    CommandQueueFull,
    UnsupportedCommand,
    InvalidServerPayload,
    AuthorityOrdinalExhausted,
    Transport,
}

public readonly record struct SessionHudFault(SessionHudFaultCode Code, string Detail);

/// <summary>
/// Projects the session cases used by the native HUD into its protocol-neutral event family.
/// It observes envelopes without consuming or retaining them, so world, inventory, quest-dialogue,
/// and protocol-error consumers receive the same message unchanged.
/// </summary>
public sealed class SessionHudAdapter : IHudSession
{
    private const int ActionSlotCount = 36;
    private const int LootItemCount = 20;
    private const int CharacterWireEquipmentCount = 20;
    private const int CharacterEquipmentCount = 21;
    private const int CharacterStatCount = 14;
    private const int QuestLogEntryCount = 20;
    private static readonly int[][] InventoryPartitions =
    [
        [12], [16], [12, 6], [16, 8], [30], [8, 8, 8, 6, 6], [30, 12],
        [12, 12, 12, 12], [30, 12, 12], [30, 30],
    ];
    private static readonly HudUnitPresentation AvatarPresentation = new(
        new HudPlateAssignment(new HudId("avatar")),
        OvertipCandidate: false);
    private static readonly HudUnitPresentation TargetPresentation = new(
        new HudPlateAssignment(new HudId("target")),
        OvertipCandidate: false);

    private static readonly HudSessionCapabilities SupportedCapabilities = new(
        HudEventFamilies.ActionSlots |
        HudEventFamilies.TargetSelection |
        HudEventFamilies.Units |
        HudEventFamilies.CombatFeedback |
        HudEventFamilies.QuestTracker |
        HudEventFamilies.Chat |
        HudEventFamilies.Inventory |
        HudEventFamilies.Loot |
        HudEventFamilies.QuestLog |
        HudEventFamilies.QuestInfo |
        HudEventFamilies.Character,
        HudCommandFamilies.All);

    private readonly object _gate = new();
    private readonly uint _sourceEpoch;
    private ulong _ownEntityId;
    private readonly SessionHudAdapterOptions _options;
    private readonly Queue<HudEvent> _reliableEvents = new();
    private readonly Dictionary<ulong, HudEvent> _snapshotEvents = [];
    private readonly Dictionary<ulong, UnitAuthority> _unitAuthorities = [];
    private readonly HashSet<ulong> _playerEntityIds = [];
    private readonly Queue<HudCommand> _commands = new();
    private ulong _selectedTargetEntityId;
    private ulong _targetRevision;
    private ulong _actionRevision;
    private ulong _inventoryRevision;
    private ulong _lootRevision;
    private ulong _questLogRevision;
    private ulong _questInfoRevision;
    private ulong _characterRevision;
    private HudItemReference _equippedBag;
    private HudChatAntiSpamCatalog? _chatAntiSpam;
    private ChatRequestLedger? _chatLedger;
    private readonly HashSet<string> _friendNames = new(StringComparer.Ordinal);
    private bool _friendNamesHaveAuthority;
    private string? _ownName;
    private ulong[] _inventoryItemInstances = [];
    private HudId[] _inventoryCooldownSpells = [];
    private ulong _nextOrdinal;
    private int _droppedSnapshots;
    private HudSessionState _state = HudSessionState.Open;
    private SessionHudFault? _fault;

    public SessionHudAdapter(
        uint sourceEpoch,
        ulong ownEntityId,
        SessionHudAdapterOptions? options = null)
        : this(sourceEpoch, options)
    {
        BindOwnEntity(ownEntityId);
    }

    public SessionHudAdapter(
        uint sourceEpoch,
        SessionHudAdapterOptions? options = null)
    {
        if (sourceEpoch == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceEpoch));
        }

        _options = options ?? new SessionHudAdapterOptions();
        _options.Validate();
        _sourceEpoch = sourceEpoch;
    }

    public HudSessionCapabilities Capabilities => SupportedCapabilities;

    public uint SourceEpoch => _sourceEpoch;

    public ulong OwnEntityId => _ownEntityId;

    public void ConfigureChat(HudChatAntiSpamCatalog antiSpam)
    {
        ArgumentNullException.ThrowIfNull(antiSpam);
        lock (_gate)
        {
            if (_state != HudSessionState.Open || _nextOrdinal != 0 || _commands.Count != 0 || _chatAntiSpam is not null)
            {
                throw new InvalidOperationException("Chat must be configured exactly once before session traffic starts.");
            }

            _chatAntiSpam = antiSpam;
            TryInitializeChatLedger();
        }
    }

    public void ReplaceFriendNames(IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        lock (_gate)
        {
            if (_state != HudSessionState.Open)
            {
                throw new InvalidOperationException("A closed HUD session cannot accept friend authority.");
            }

            if (names.Any(name => string.IsNullOrEmpty(name) || !HudChatText.IsWellFormedUtf16(name)))
            {
                throw new ArgumentException("Friend names must be nonempty valid UTF-16 strings.", nameof(names));
            }

            _friendNames.Clear();
            _friendNames.UnionWith(names);
            _friendNamesHaveAuthority = true;
        }
    }

    /// <summary>Binds the entity admitted by EnterZoneResponse before the receive loop starts.</summary>
    public void BindOwnEntity(ulong ownEntityId)
    {
        if (ownEntityId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownEntityId));
        }

        lock (_gate)
        {
            if (_state != HudSessionState.Open || _nextOrdinal != 0 || _commands.Count != 0)
            {
                throw new InvalidOperationException("HUD session identity must be bound before traffic starts.");
            }

            if (_ownEntityId != 0 && _ownEntityId != ownEntityId)
            {
                throw new InvalidOperationException("HUD session identity is already bound.");
            }

            _ownEntityId = ownEntityId;
            TryInitializeChatLedger();
        }
    }

    public HudSessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public SessionHudFault? Fault
    {
        get
        {
            lock (_gate)
            {
                return _fault;
            }
        }
    }

    /// <summary>
    /// Observes one envelope. The caller retains ownership and must continue routing the same
    /// instance to every other session consumer, regardless of the returned projection status.
    /// </summary>
    public SessionHudObservation Observe(ServerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            if (_state != HudSessionState.Open)
            {
                return SessionHudObservation.Terminal;
            }

            if (_ownEntityId == 0)
            {
                return Fail(SessionHudFaultCode.InvalidServerPayload, "HUD session received traffic before entity admission.");
            }

            try
            {
                return message.PayloadCase switch
                {
                    ServerMessage.PayloadOneofCase.SnapshotBatch => ObserveSnapshot(message),
                    ServerMessage.PayloadOneofCase.SpawnEvent => ObserveSpawn(message),
                    ServerMessage.PayloadOneofCase.DespawnEvent => ObserveDespawn(message),
                    ServerMessage.PayloadOneofCase.CombatEvent => ObserveCombat(message),
                    ServerMessage.PayloadOneofCase.DeathEvent => ObserveDeath(message),
                    ServerMessage.PayloadOneofCase.QuestStateUpdate => ObserveQuest(message),
                    ServerMessage.PayloadOneofCase.ActionBarReplacement => ObserveActionBar(message.ActionBarReplacement),
                    ServerMessage.PayloadOneofCase.TargetStateReplacement => ObserveTarget(message.TargetStateReplacement),
                    ServerMessage.PayloadOneofCase.InventoryStateReplacement =>
                        ObserveInventory(message.InventoryStateReplacement),
                    ServerMessage.PayloadOneofCase.InventoryMoveResult => ObserveInventoryMove(message.InventoryMoveResult),
                    ServerMessage.PayloadOneofCase.InventorySlotCooldownUpdate =>
                        ObserveInventoryCooldown(message.InventorySlotCooldownUpdate),
                    ServerMessage.PayloadOneofCase.LootStateReplacement => ObserveLoot(message.LootStateReplacement),
                    ServerMessage.PayloadOneofCase.QuestLogReplacement => ObserveQuestLog(message.QuestLogReplacement),
                    ServerMessage.PayloadOneofCase.QuestInfoReplacement => ObserveQuestInfo(message.QuestInfoReplacement),
                    ServerMessage.PayloadOneofCase.CharacterStateReplacement =>
                        ObserveCharacter(message.CharacterStateReplacement),
                    ServerMessage.PayloadOneofCase.ChatDelivery => ObserveChatDelivery(message.ChatDelivery),
                    ServerMessage.PayloadOneofCase.ChatRejection => ObserveChatRejection(message.ChatRejection),
                    _ => SessionHudObservation.NotSubscribed,
                };
            }
            catch (ArgumentException exception)
            {
                return Fail(SessionHudFaultCode.InvalidServerPayload, exception.Message);
            }
            catch (OverflowException exception)
            {
                return Fail(SessionHudFaultCode.InvalidServerPayload, exception.Message);
            }
        }
    }

    public HudSessionRead Read(Span<HudEvent> destination)
    {
        lock (_gate)
        {
            int count = 0;
            while (count < destination.Length && TryTakeNextEvent(out HudEvent item))
            {
                destination[count++] = item;
            }

            int dropped = _droppedSnapshots;
            _droppedSnapshots = 0;
            return new HudSessionRead(count, dropped, _state);
        }
    }

    public bool TryWrite(in HudCommand command)
    {
        lock (_gate)
        {
            if (_state != HudSessionState.Open)
            {
                return false;
            }

            if (!IsSupported(command))
            {
                Fail(SessionHudFaultCode.UnsupportedCommand, $"HUD command {command.Kind} is not supported by this session.");
                return false;
            }

            if (_commands.Count >= _options.CommandCapacity)
            {
                Fail(SessionHudFaultCode.CommandQueueFull, $"HUD command queue reached {_options.CommandCapacity} entries.");
                return false;
            }

            _commands.Enqueue(command);
            return true;
        }
    }

    /// <summary>Lets the owning session loop take one typed command without learning HUD rules.</summary>
    public bool TryTakeCommand(out HudCommand command)
    {
        lock (_gate)
        {
            if (_state != HudSessionState.Open || !_commands.TryDequeue(out command))
            {
                command = default;
                return false;
            }

            return true;
        }
    }

    public bool TryCreateChatOutbound(
        HudChatSubmission submission,
        long sentAtUnixMilliseconds,
        out ChatOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(submission);
        lock (_gate)
        {
            if (_state != HudSessionState.Open || _chatLedger is null || _chatAntiSpam is null)
            {
                outbound = default;
                return false;
            }

            IReadOnlyCollection<string> friends = _friendNamesHaveAuthority ? _friendNames : Array.Empty<string>();
            bool senderAlive = !_unitAuthorities.TryGetValue(_ownEntityId, out UnitAuthority own) ||
                (!own.Removed && own.Health > 0);
            outbound = _chatLedger.CreateOutbound(
                submission,
                sentAtUnixMilliseconds,
                senderAlive,
                _chatAntiSpam,
                friends);
            if (!TryNextStamp(outbound.Request.RequestId, out HudStamp stamp) ||
                !TryEnqueueReliable(HudEvent.ChatReceived(stamp, outbound.LocalProjection)))
            {
                outbound = default;
                return false;
            }

            return true;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_state != HudSessionState.Open)
            {
                return;
            }

            _state = HudSessionState.Closed;
            _commands.Clear();
        }
    }

    public void ReportTransportFault(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        lock (_gate)
        {
            Fail(SessionHudFaultCode.Transport, detail);
        }
    }

    private SessionHudObservation ObserveSnapshot(ServerMessage message)
    {
        SnapshotBatch batch = message.SnapshotBatch;
        ulong revision = ResolveSnapshotRevision(message.ServerTick, batch.ServerTick);
        var mapped = new List<HudEvent>(batch.Entities.Count);
        var ids = new HashSet<ulong>();
        int newAuthorities = 0;
        foreach (EntitySnapshot entity in batch.Entities)
        {
            ValidateEntity(entity);
            TrackEntityKind(entity);
            if (!ids.Add(entity.EntityId))
            {
                throw new ArgumentException($"Snapshot {revision} repeats entity {entity.EntityId}.");
            }

            if (!_unitAuthorities.ContainsKey(entity.EntityId))
            {
                newAuthorities++;
            }

            if (!TryNextStamp(revision, out HudStamp stamp))
            {
                return SessionHudObservation.Terminal;
            }

            mapped.Add(MapUnit(entity, stamp));
        }

        if (_unitAuthorities.Count + newAuthorities > _options.SnapshotEntityCapacity)
        {
            return Fail(
                SessionHudFaultCode.SnapshotEntityCapacityExceeded,
                $"HUD entity authority table exceeded {_options.SnapshotEntityCapacity} entries.");
        }

        foreach (HudEvent item in mapped)
        {
            if (_unitAuthorities.TryGetValue(item.EntityId, out UnitAuthority authority) &&
                authority.Stamp.CompareTo(item.Stamp) > 0)
            {
                CountDroppedSnapshot();
                continue;
            }

            _unitAuthorities[item.EntityId] = UnitAuthority.From(item, removed: false);
            if (_snapshotEvents.TryGetValue(item.EntityId, out HudEvent pending))
            {
                if (pending.Stamp.CompareTo(item.Stamp) <= 0)
                {
                    _snapshotEvents[item.EntityId] = item;
                }

                CountDroppedSnapshot();
            }
            else
            {
                _snapshotEvents.Add(item.EntityId, item);
            }
        }

        return mapped.Count == 0 ? SessionHudObservation.Observed : SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveSpawn(ServerMessage message)
    {
        EntitySnapshot? entity = message.SpawnEvent.Entity;
        if (entity is null)
        {
            throw new ArgumentException("Spawn event has no entity.");
        }

        ValidateEntity(entity);
        TrackEntityKind(entity);
        if (!EnsureAuthorityCapacity(entity.EntityId))
        {
            return SessionHudObservation.Terminal;
        }

        if (!TryNextStamp(message.ServerTick, out HudStamp stamp))
        {
            return SessionHudObservation.Terminal;
        }

        HudEvent item = MapUnit(entity, stamp);
        if (!TryEnqueueReliable(item))
        {
            return SessionHudObservation.Terminal;
        }

        ApplyReliableAuthority(item, removed: false);
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveDespawn(ServerMessage message)
    {
        ulong entityId = message.DespawnEvent.EntityId;
        if (entityId == 0)
        {
            throw new ArgumentException("Despawn event has no entity identifier.");
        }

        if (!EnsureAuthorityCapacity(entityId) || !TryNextStamp(message.ServerTick, out HudStamp stamp))
        {
            return SessionHudObservation.Terminal;
        }

        HudEvent item = HudEvent.UnitRemoved(stamp, entityId);
        _playerEntityIds.Remove(entityId);
        if (!TryEnqueueReliable(item))
        {
            return SessionHudObservation.Terminal;
        }

        ApplyReliableAuthority(item, removed: true);
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveCombat(ServerMessage message)
    {
        CombatEvent combat = message.CombatEvent;
        var mapped = new List<HudEvent>(2);
        if (combat.Rejection == AbilityRejection.Unspecified || !Enum.IsDefined(combat.Rejection))
        {
            throw new ArgumentException($"Combat event carries unsupported rejection {combat.Rejection}.");
        }

        if (combat.Rejection == AbilityRejection.None && combat.Damage > 0)
        {
            if (!TryNextStamp(message.ServerTick, out HudStamp feedbackStamp))
            {
                return SessionHudObservation.Terminal;
            }

            mapped.Add(HudEvent.FeedbackRaised(
                feedbackStamp,
                EventId("combat", feedbackStamp, combat.CasterId, combat.TargetId, combat.AbilityId),
                combat.TargetId == _ownEntityId ? HudFeedbackKind.Avatar : HudFeedbackKind.Enemy,
                combat.TargetId,
                combat.Damage));
        }

        HudEvent? unitChange = null;
        if (combat.Rejection == AbilityRejection.None &&
            combat.TargetId != 0 &&
            _unitAuthorities.TryGetValue(combat.TargetId, out UnitAuthority authority) &&
            !authority.Removed)
        {
            if (!TryNextStamp(message.ServerTick, out HudStamp unitStamp))
            {
                return SessionHudObservation.Terminal;
            }

            unitChange = HudEvent.UnitChanged(
                unitStamp,
                combat.TargetId,
                authority.Name,
                combat.TargetHealth,
                combat.TargetMaxHealth,
                authority.Presentation);
            mapped.Add(unitChange.Value);
        }

        if (mapped.Count == 0)
        {
            return SessionHudObservation.Observed;
        }

        if (!TryEnqueueReliable(mapped))
        {
            return SessionHudObservation.Terminal;
        }

        if (unitChange is HudEvent changed)
        {
            ApplyReliableAuthority(changed, removed: false);
        }

        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveDeath(ServerMessage message)
    {
        ulong entityId = message.DeathEvent.VictimEntityId;
        if (entityId == 0)
        {
            throw new ArgumentException("Death event has no victim entity identifier.");
        }

        if (!_unitAuthorities.TryGetValue(entityId, out UnitAuthority authority) || authority.Removed)
        {
            return SessionHudObservation.Observed;
        }

        if (!TryNextStamp(message.ServerTick, out HudStamp stamp))
        {
            return SessionHudObservation.Terminal;
        }

        HudEvent item = HudEvent.UnitChanged(
            stamp,
            entityId,
            authority.Name,
            0,
            authority.MaximumHealth,
            authority.Presentation);
        if (!TryEnqueueReliable(item))
        {
            return SessionHudObservation.Terminal;
        }

        ApplyReliableAuthority(item, removed: false);
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveQuest(ServerMessage message)
    {
        QuestStateUpdate quest = message.QuestStateUpdate;
        if (quest.Refusal != QuestRefusal.None)
        {
            return SessionHudObservation.Observed;
        }

        if (string.IsNullOrWhiteSpace(quest.QuestId))
        {
            throw new ArgumentException("Quest update has no quest identifier.");
        }

        var mapped = new List<HudEvent>(2);
        if (!TryNextStamp(message.ServerTick, out HudStamp questStamp))
        {
            return SessionHudObservation.Terminal;
        }

        HudId questId = new(quest.QuestId);
        switch (quest.State)
        {
            case QuestState.Accepted:
            case QuestState.InProgress:
            case QuestState.Completable:
                mapped.Add(HudEvent.QuestTracked(
                    questStamp,
                    new HudQuestSnapshot(
                        questId,
                        new HudId($"{quest.QuestId}.title"),
                        quest.State == QuestState.Completable,
                        quest.Objectives.Select(objective => new HudQuestObjective(
                            objective.Index,
                            new HudId(string.IsNullOrWhiteSpace(objective.CounterKey)
                                ? $"{quest.QuestId}.objective.{objective.Index}"
                                : objective.CounterKey),
                            objective.Counter,
                            objective.Limit,
                            objective.ShowCount)).ToArray())));
                break;
            case QuestState.Unavailable:
            case QuestState.Offered:
            case QuestState.TurnedIn:
            case QuestState.Abandoned:
                mapped.Add(HudEvent.QuestUntracked(questStamp, questId));
                break;
            default:
                throw new ArgumentException($"Quest {quest.QuestId} carries unsupported state {quest.State}.");
        }

        if (quest.State == QuestState.TurnedIn && quest.Experience > 0)
        {
            if (quest.Experience > int.MaxValue)
            {
                throw new OverflowException($"Quest {quest.QuestId} experience exceeds the HUD amount range.");
            }

            if (!TryNextStamp(message.ServerTick, out HudStamp feedbackStamp))
            {
                return SessionHudObservation.Terminal;
            }

            mapped.Add(HudEvent.FeedbackRaised(
                feedbackStamp,
                EventId("experience", feedbackStamp, _ownEntityId, 0, quest.QuestId),
                HudFeedbackKind.Experience,
                _ownEntityId,
                checked((int)quest.Experience)));
        }

        return TryEnqueueReliable(mapped) ? SessionHudObservation.Projected : SessionHudObservation.Terminal;
    }

    private SessionHudObservation ObserveActionBar(ActionBarReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateRevision(replacement.Revision, "Action bar");
        ValidateDefinedRefusal(replacement.ActivationRefusal, ActionActivationRefusal.Unspecified, "Action bar");
        if (replacement.Revision <= _actionRevision)
        {
            return SessionHudObservation.Observed;
        }

        if (replacement.Slots.Count != ActionSlotCount)
        {
            throw new ArgumentException($"Action bar has {replacement.Slots.Count} slots; expected exactly {ActionSlotCount}.");
        }

        var ordered = new ActionBarSlotState?[ActionSlotCount];
        foreach (ActionBarSlotState slot in replacement.Slots)
        {
            int index = CheckedInt(slot.SlotIndex, "Action slot index");
            if ((uint)index >= ActionSlotCount || ordered[index] is not null)
            {
                throw new ArgumentException($"Action bar slot {slot.SlotIndex} is out of range or duplicated.");
            }

            ValidateDefinedRefusal(slot.UnavailableReason, ActionUnavailableReason.Unspecified, $"Action slot {index}");
            int remaining = CheckedMilliseconds(slot.CooldownRemainingMilliseconds, $"Action slot {index} remaining cooldown");
            int duration = CheckedMilliseconds(slot.CooldownDurationMilliseconds, $"Action slot {index} cooldown duration");
            if (remaining > duration)
            {
                throw new ArgumentException($"Action slot {index} has a remaining cooldown longer than its duration.");
            }

            bool empty = string.IsNullOrWhiteSpace(slot.AbilityId);
            if (empty && (remaining != 0 || duration != 0 || slot.Available ||
                slot.UnavailableReason != ActionUnavailableReason.EmptySlot))
            {
                throw new ArgumentException($"Empty action slot {index} carries ability state.");
            }

            if (!empty && ((slot.Available && slot.UnavailableReason != ActionUnavailableReason.None) ||
                (!slot.Available && slot.UnavailableReason == ActionUnavailableReason.None)))
            {
                throw new ArgumentException($"Action slot {index} availability and refusal disagree.");
            }

            ordered[index] = slot;
        }

        var mapped = new List<HudEvent>(ActionSlotCount);
        for (int index = 0; index < ordered.Length; index++)
        {
            ActionBarSlotState slot = ordered[index]!;
            if (!TryNextStamp(replacement.Revision, out HudStamp stamp))
            {
                return SessionHudObservation.Terminal;
            }

            mapped.Add(string.IsNullOrWhiteSpace(slot.AbilityId)
                ? HudEvent.ActionSlotCleared(stamp, index)
                : HudEvent.ActionSlotChanged(
                    stamp,
                    index,
                    new HudId(slot.AbilityId),
                    CheckedMilliseconds(slot.CooldownRemainingMilliseconds, "Action cooldown"),
                    slot.Available,
                    CheckedMilliseconds(slot.CooldownDurationMilliseconds, "Action cooldown duration")));
        }

        if (!TryEnqueueReliable(mapped))
        {
            return SessionHudObservation.Terminal;
        }

        _actionRevision = replacement.Revision;
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveTarget(TargetStateReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateRevision(replacement.Revision, "Target selection");
        ValidateDefinedRefusal(replacement.Refusal, TargetSelectRefusal.Unspecified, "Target selection");
        if (replacement.Revision <= _targetRevision)
        {
            return SessionHudObservation.Observed;
        }

        if (!replacement.HasAuthority &&
            (replacement.Refusal == TargetSelectRefusal.None || replacement.SelectedEntityId != 0))
        {
            throw new ArgumentException("Target selection without authority must carry a typed refusal and no selected entity.");
        }

        ulong nextEntity = replacement.HasAuthority ? replacement.SelectedEntityId : _selectedTargetEntityId;
        var mapped = new List<HudEvent>(3);
        if (replacement.HasAuthority && nextEntity != _selectedTargetEntityId)
        {
            AddPresentationChange(mapped, _selectedTargetEntityId, replacement.Revision, selectedPresentation: false);
            AddPresentationChange(mapped, nextEntity, replacement.Revision, selectedPresentation: true);
        }

        if (!TryNextStamp(replacement.Revision, out HudStamp selectionStamp))
        {
            return SessionHudObservation.Terminal;
        }

        mapped.Add(HudEvent.TargetSelectionChanged(
            selectionStamp,
            nextEntity,
            MapTargetRefusal(replacement.Refusal)));
        if (!TryEnqueueReliable(mapped))
        {
            return SessionHudObservation.Terminal;
        }

        if (replacement.HasAuthority && nextEntity != _selectedTargetEntityId)
        {
            ApplyMappedUnitChanges(mapped);
            _selectedTargetEntityId = nextEntity;
        }

        _targetRevision = replacement.Revision;
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveInventoryMove(InventoryMoveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateDefinedRefusal(result.Refusal, InventoryMoveRefusal.Unspecified, "Inventory move");
        if (result.RequestId == 0 || result.Replacement is null)
        {
            throw new ArgumentException("Inventory move results need a request and full replacement.");
        }

        return ObserveInventory(result.Replacement);
    }

    private SessionHudObservation ObserveInventory(InventoryStateReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateRevision(replacement.Revision, "Inventory");
        if (replacement.Revision <= _inventoryRevision)
        {
            return SessionHudObservation.Observed;
        }

        int capacity = CheckedInt(replacement.Capacity, "Inventory capacity");
        int layoutCapacity = (int)replacement.LayoutId;
        int layoutIndex = Array.IndexOf([12, 16, 18, 24, 30, 36, 42, 48, 54, 60], layoutCapacity);
        if (!Enum.IsDefined(replacement.LayoutId) || layoutIndex < 0 || capacity != layoutCapacity ||
            capacity > HudProduct.InventorySlotCount)
        {
            throw new ArgumentException("Inventory layout and capacity do not name the same authored multibag.");
        }

        int[] expectedPartitions = InventoryPartitions[layoutIndex];
        if (replacement.PartitionSizes.Count != expectedPartitions.Length ||
            !replacement.PartitionSizes.Select(CheckedPartition).SequenceEqual(expectedPartitions))
        {
            throw new ArgumentException("Inventory partitions do not match the authored multibag layout.");
        }

        if (_equippedBag.InstanceId == 0 || replacement.EquippedBagItemId != _equippedBag.InstanceId)
        {
            throw new ArgumentException("Inventory equipped-bag reference does not match character authority.");
        }

        if (replacement.Currency < 0)
        {
            throw new ArgumentException("Inventory currency cannot be negative.");
        }

        var slots = new HudItemStack?[capacity];
        var cooldowns = new HudInventoryCooldown?[capacity];
        var itemInstances = new ulong[capacity];
        var cooldownSpells = new HudId[capacity];
        foreach (InventorySlotState slot in replacement.Slots)
        {
            int index = CheckedInt(slot.SlotIndex, "Inventory slot index");
            if ((uint)index >= (uint)capacity || slots[index] is not null || slot.Item is null)
            {
                throw new ArgumentException($"Inventory slot {slot.SlotIndex} is out of range, duplicated, or empty in the sparse vector.");
            }

            HudItemStack item = MapItem(slot.Item, $"Inventory slot {index}");
            slots[index] = item;
            itemInstances[index] = item.InstanceId;
            if (slot.SpellCooldown is { } cooldown)
            {
                HudInventoryCooldown mappedCooldown = MapCooldown(cooldown, $"Inventory slot {index}");
                cooldowns[index] = mappedCooldown;
                cooldownSpells[index] = mappedCooldown.SpellId;
            }
        }

        var snapshot = new HudInventorySnapshot(capacity, replacement.Currency, _equippedBag, slots, cooldowns);
        if (!TryNextStamp(replacement.Revision, out HudStamp stamp) ||
            !TryEnqueueReliable(HudEvent.InventoryReplaced(stamp, snapshot)))
        {
            return SessionHudObservation.Terminal;
        }

        _inventoryRevision = replacement.Revision;
        _inventoryItemInstances = itemInstances;
        _inventoryCooldownSpells = cooldownSpells;
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveInventoryCooldown(InventorySlotCooldownUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateRevision(update.InventoryRevision, "Inventory cooldown");
        if (update.ItemInstanceId == 0 || update.SlotIndex >= HudProduct.InventorySlotCount)
        {
            throw new ArgumentException("Inventory cooldown has an invalid item or slot.");
        }

        int slot = CheckedInt(update.SlotIndex, "Inventory cooldown slot");
        if (update.InventoryRevision != _inventoryRevision || slot >= _inventoryItemInstances.Length ||
            _inventoryItemInstances[slot] != update.ItemInstanceId)
        {
            return SessionHudObservation.Observed;
        }

        HudEvent item;
        HudId nextSpell = HudId.Empty;
        if (update.SpellCooldown is null)
        {
            HudId oldSpell = _inventoryCooldownSpells[slot];
            if (oldSpell.IsEmpty)
            {
                return SessionHudObservation.Observed;
            }

            if (!TryNextStamp(update.InventoryRevision, out HudStamp stamp))
            {
                return SessionHudObservation.Terminal;
            }

            item = HudEvent.InventoryCooldownFinished(stamp, slot, oldSpell);
        }
        else
        {
            ItemSlotSpellCooldownState cooldown = update.SpellCooldown;
            if (string.IsNullOrWhiteSpace(cooldown.ProductSpellId))
            {
                throw new ArgumentException("Inventory cooldown has no product spell identifier.");
            }

            int remaining = CheckedMilliseconds(cooldown.RemainingMilliseconds, "Inventory cooldown remaining");
            int duration = CheckedMilliseconds(cooldown.DurationMilliseconds, "Inventory cooldown duration");
            if (remaining > duration)
            {
                throw new ArgumentException("Inventory cooldown remaining time exceeds its duration.");
            }

            HudId spell = new(cooldown.ProductSpellId);
            if (!TryNextStamp(update.InventoryRevision, out HudStamp stamp))
            {
                return SessionHudObservation.Terminal;
            }

            if (remaining == 0)
            {
                item = HudEvent.InventoryCooldownFinished(stamp, slot, spell);
            }
            else
            {
                item = HudEvent.InventoryCooldownStarted(stamp, slot, spell, remaining, duration);
                nextSpell = spell;
            }
        }

        if (!TryEnqueueReliable(item))
        {
            return SessionHudObservation.Terminal;
        }

        _inventoryCooldownSpells[slot] = nextSpell;
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveLoot(LootStateReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateRevision(replacement.Revision, "Loot");
        ValidateDefinedRefusal(replacement.Refusal, LootUiRefusal.Unspecified, "Loot");
        if (replacement.Revision <= _lootRevision)
        {
            return SessionHudObservation.Observed;
        }

        if (replacement.PageSize != HudProduct.LootPageSize || replacement.TotalCount > LootItemCount ||
            replacement.Items.Count != replacement.TotalCount || replacement.Money < 0 ||
            (replacement.Open && replacement.LootEntityId == 0))
        {
            throw new ArgumentException("Loot replacement violates the authored four-by-five bag shape.");
        }

        var ordered = new HudLootItem[replacement.Items.Count];
        var occupied = new bool[replacement.Items.Count];
        foreach (LootItemState item in replacement.Items)
        {
            if (item.ItemIndex < 0 || item.ItemIndex >= ordered.Length || occupied[item.ItemIndex] ||
                string.IsNullOrWhiteSpace(item.ProductItemId) || item.Count == 0 || item.Count > int.MaxValue)
            {
                throw new ArgumentException($"Loot item index {item.ItemIndex} is invalid or duplicated.");
            }

            occupied[item.ItemIndex] = true;
            ordered[item.ItemIndex] = new HudLootItem(new HudId(item.ProductItemId), checked((int)item.Count), item.IsCursed);
        }

        var snapshot = new HudLootSnapshot(
            replacement.LootEntityId,
            replacement.Money,
            ordered,
            MapLootRefusal(replacement.Refusal),
            replacement.Open);
        if (!TryNextStamp(replacement.Revision, out HudStamp stamp) ||
            !TryEnqueueReliable(HudEvent.LootReplaced(stamp, snapshot)))
        {
            return SessionHudObservation.Terminal;
        }

        _lootRevision = replacement.Revision;
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveQuestLog(QuestLogReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateRevision(replacement.Revision, "Quest log");
        ValidateDefinedRefusal(replacement.CommandRefusal, QuestLogCommandRefusal.Unspecified, "Quest-log command");
        if (replacement.Revision <= _questLogRevision)
        {
            return SessionHudObservation.Observed;
        }

        if (replacement.VisibleQuests.Count > QuestLogEntryCount ||
            replacement.BookmarkQuestIds.Count > HudProduct.QuestLogBookmarkCount ||
            replacement.DailyCount > replacement.DailyLimit || replacement.ShareInvites.Count > 1)
        {
            throw new ArgumentException("Quest log exceeds an authored collection or daily limit.");
        }

        if (replacement.CommandRefusal != QuestLogCommandRefusal.None &&
            string.IsNullOrWhiteSpace(replacement.CommandQuestId))
        {
            throw new ArgumentException("A refused quest-log command must identify its quest.");
        }

        var questIds = new HashSet<string>(StringComparer.Ordinal);
        var quests = new HudQuestDocument[replacement.VisibleQuests.Count];
        for (int index = 0; index < replacement.VisibleQuests.Count; index++)
        {
            QuestLogEntry entry = replacement.VisibleQuests[index];
            if (string.IsNullOrWhiteSpace(entry.QuestId) || !questIds.Add(entry.QuestId))
            {
                throw new ArgumentException("Quest-log entries need unique product quest identifiers.");
            }

            quests[index] = MapQuestLogDocument(entry);
        }

        if (!string.IsNullOrWhiteSpace(replacement.SelectedQuestId) && !questIds.Contains(replacement.SelectedQuestId))
        {
            throw new ArgumentException("The selected quest is absent from the visible quest log.");
        }

        foreach (string bookmark in replacement.BookmarkQuestIds)
        {
            if (string.IsNullOrWhiteSpace(bookmark) || !questIds.Contains(bookmark))
            {
                throw new ArgumentException("Quest-log bookmarks must reference visible quests.");
            }
        }

        foreach (QuestShareResult result in replacement.ShareResults)
        {
            ValidateDefinedRefusal(result.Refusal, QuestShareRefusal.Unspecified, "Quest share result");
            if (result.RequestId == 0 || string.IsNullOrWhiteSpace(result.QuestId))
            {
                throw new ArgumentException("Quest share results need request and quest identifiers.");
            }
        }

        HudQuestShareInvitation? invitation = null;
        if (replacement.ShareInvites.Count == 1)
        {
            QuestShareInvite invite = replacement.ShareInvites[0];
            if (invite.InviteId == 0 || string.IsNullOrWhiteSpace(invite.QuestId) ||
                invite.SenderEntityId == 0 || string.IsNullOrWhiteSpace(invite.SenderName) ||
                invite.RemainingMilliseconds == 0)
            {
                throw new ArgumentException("Quest share invitation is incomplete.");
            }

            invitation = new HudQuestShareInvitation(
                new HudId(FormattableString.Invariant($"quest-share.{invite.InviteId}")),
                new HudId(invite.QuestId),
                new HudId(invite.SenderName));
        }

        var snapshot = new HudQuestLogSnapshot(quests, shareInvitation: invitation);
        if (!TryNextStamp(replacement.Revision, out HudStamp stamp) ||
            !TryEnqueueReliable(HudEvent.QuestLogReplaced(stamp, snapshot)))
        {
            return SessionHudObservation.Terminal;
        }

        _questLogRevision = replacement.Revision;
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveQuestInfo(QuestInfoReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateRevision(replacement.Revision, "Quest info");
        ValidateDefinedRefusal(replacement.Refusal, QuestInfoRefusal.Unspecified, "Quest info");
        if (replacement.Revision <= _questInfoRevision)
        {
            return SessionHudObservation.Observed;
        }

        HudQuestRefusal refusal = MapQuestRefusal(replacement.Refusal);
        HudQuestInfoSnapshot snapshot;
        switch (replacement.Mode)
        {
            case QuestInfoMode.None:
                if (replacement.NpcEntityId != 0 || replacement.Info is not null ||
                    replacement.Progress is not null || replacement.Reward is not null)
                {
                    throw new ArgumentException("Closed quest info retains interaction state.");
                }

                snapshot = new HudQuestInfoSnapshot(HudQuestInfoMode.None, null, 0, refusal: refusal);
                break;
            case QuestInfoMode.Offer:
            case QuestInfoMode.TurnIn:
                if (replacement.NpcEntityId == 0 || replacement.Info is null ||
                    replacement.Progress is null || replacement.Reward is null)
                {
                    throw new ArgumentException("Open quest info needs NPC, definition, progress, and reward authority.");
                }

                ValidateQuestIdentity(replacement);
                HudQuestClientState state = replacement.Mode == QuestInfoMode.Offer
                    ? HudQuestClientState.Offered
                    : HudQuestClientState.Completable;
                if ((replacement.Mode == QuestInfoMode.Offer && replacement.Progress.State != QuestUiState.InProgress) ||
                    (replacement.Mode == QuestInfoMode.TurnIn && replacement.Progress.State != QuestUiState.ReadyToReturn))
                {
                    throw new ArgumentException("Quest-info mode and progress state disagree.");
                }

                HudQuestRewardSnapshot reward = MapQuestReward(replacement.Reward);
                HudQuestDocument quest = MapQuestInfoDocument(replacement.Info, replacement.Progress, state, reward);
                snapshot = new HudQuestInfoSnapshot(
                    replacement.Mode == QuestInfoMode.Offer ? HudQuestInfoMode.Offer : HudQuestInfoMode.TurnIn,
                    quest,
                    replacement.NpcEntityId,
                    reward,
                    refusal);
                break;
            default:
                throw new ArgumentException($"Quest info carries unsupported mode {replacement.Mode}.");
        }

        if (!TryNextStamp(replacement.Revision, out HudStamp stamp) ||
            !TryEnqueueReliable(HudEvent.QuestInfoReplaced(stamp, snapshot)))
        {
            return SessionHudObservation.Terminal;
        }

        _questInfoRevision = replacement.Revision;
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveCharacter(CharacterStateReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateRevision(replacement.Revision, "Character");
        if (replacement.Revision <= _characterRevision)
        {
            return SessionHudObservation.Observed;
        }

        if (replacement.CharacterEntityId != _ownEntityId || string.IsNullOrWhiteSpace(replacement.Name) ||
            replacement.Level == 0 || replacement.Level > int.MaxValue ||
            replacement.Equipment.Count != CharacterWireEquipmentCount ||
            replacement.Stats.Count != CharacterStatCount || replacement.Bag is null)
        {
            throw new ArgumentException("Character replacement does not match the admitted character or authored census.");
        }

        var equipment = new HudItemStack?[CharacterEquipmentCount];
        var occupiedEquipment = new bool[CharacterEquipmentCount];
        occupiedEquipment[HudProduct.CharacterBagSlot] = true;
        foreach (EquipmentSlotState slot in replacement.Equipment)
        {
            int index = MapEquipmentSlot(slot.Slot);
            if (occupiedEquipment[index])
            {
                throw new ArgumentException($"Character equipment role {slot.Slot} is duplicated.");
            }

            occupiedEquipment[index] = true;
            if (slot.Item is not null)
            {
                equipment[index] = MapItem(slot.Item, $"Character equipment {slot.Slot}");
            }
        }

        if (occupiedEquipment.Any(occupied => !occupied))
        {
            throw new ArgumentException("Character replacement omits an authored equipment role.");
        }

        HudItemStack bag = MapItem(replacement.Bag, "Character bag");
        equipment[HudProduct.CharacterBagSlot] = bag;
        var stats = new HudCharacterStat[CharacterStatCount];
        var occupiedStats = new bool[CharacterStatCount];
        foreach (CharacterStatState stat in replacement.Stats)
        {
            int index = (int)stat.Stat;
            if (!Enum.IsDefined(stat.Stat) || (uint)index >= CharacterStatCount || occupiedStats[index])
            {
                throw new ArgumentException($"Character stat {stat.Stat} is invalid or duplicated.");
            }

            float? baseValue = stat.HasBase ? stat.Base : null;
            float? effectiveValue = stat.HasEffective ? stat.Effective : null;
            float? longTermValue = stat.HasLongTerm ? stat.LongTerm : null;
            if ((baseValue.HasValue && !float.IsFinite(baseValue.Value)) ||
                (effectiveValue.HasValue && !float.IsFinite(effectiveValue.Value)) ||
                (longTermValue.HasValue && !float.IsFinite(longTermValue.Value)))
            {
                throw new ArgumentException($"Character stat {stat.Stat} contains a non-finite value.");
            }

            occupiedStats[index] = true;
            stats[index] = new HudCharacterStat(
                new HudId(FormattableString.Invariant($"character-stat-{index + 1:00}")),
                baseValue,
                effectiveValue,
                longTermValue);
        }

        if (occupiedStats.Any(occupied => !occupied))
        {
            throw new ArgumentException("Character replacement omits an authored stat.");
        }

        var snapshot = new HudCharacterSnapshot(
            new HudId(replacement.Name),
            checked((int)replacement.Level),
            equipment,
            stats);
        _ownName = replacement.Name;
        TryInitializeChatLedger();
        if (!TryNextStamp(replacement.Revision, out HudStamp stamp) ||
            !TryEnqueueReliable(HudEvent.CharacterReplaced(stamp, snapshot)))
        {
            return SessionHudObservation.Terminal;
        }

        _characterRevision = replacement.Revision;
        _equippedBag = new HudItemReference(bag.ItemId, bag.InstanceId);
        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveChatDelivery(ChatDelivery delivery)
    {
        if (_chatLedger is null || _chatAntiSpam is null)
        {
            throw new ArgumentException("Chat delivery arrived before native chat identity and product configuration.");
        }

        if (!_chatLedger.AcceptRemoteDelivery(delivery))
        {
            return SessionHudObservation.Observed;
        }

        bool senderIsPlayer = _playerEntityIds.Contains(delivery.SenderEntityId) &&
            _unitAuthorities.TryGetValue(delivery.SenderEntityId, out UnitAuthority sender) && !sender.Removed;
        IReadOnlyCollection<string> friends = _friendNamesHaveAuthority ? _friendNames : Array.Empty<string>();
        HudChatMessage mapped = ChatProtocolMapper.FromDelivery(delivery, senderIsPlayer, _chatAntiSpam, friends);
        if (!TryNextStamp(checked((ulong)delivery.SentAtUnixMilliseconds), out HudStamp stamp) ||
            !TryEnqueueReliable(HudEvent.ChatReceived(stamp, mapped)))
        {
            return SessionHudObservation.Terminal;
        }

        return SessionHudObservation.Projected;
    }

    private SessionHudObservation ObserveChatRejection(ChatRejection rejection)
    {
        if (_chatLedger is null)
        {
            throw new ArgumentException("Chat rejection arrived before native chat identity and product configuration.");
        }

        if (!_chatLedger.TryCorrelateRejection(rejection, out HudChatRejection mapped))
        {
            throw new ArgumentException("Chat rejection does not correlate to an outstanding authored request.");
        }

        if (!TryNextStamp(rejection.RequestId, out HudStamp stamp) ||
            !TryEnqueueReliable(HudEvent.ChatRejected(stamp, mapped)))
        {
            return SessionHudObservation.Terminal;
        }

        return SessionHudObservation.Projected;
    }

    private void TryInitializeChatLedger()
    {
        if (_chatLedger is null && _chatAntiSpam is not null && _ownEntityId != 0 && !string.IsNullOrEmpty(_ownName))
        {
            _chatLedger = new ChatRequestLedger(_ownEntityId, _ownName, _options.CommandCapacity);
        }
    }

    private void AddPresentationChange(
        ICollection<HudEvent> destination,
        ulong entityId,
        ulong revision,
        bool selectedPresentation)
    {
        if (entityId == 0 || !_unitAuthorities.TryGetValue(entityId, out UnitAuthority authority) || authority.Removed)
        {
            return;
        }

        if (!TryNextStamp(revision, out HudStamp stamp))
        {
            return;
        }

        HudUnitPresentation presentation = entityId == _ownEntityId
            ? AvatarPresentation
            : selectedPresentation ? TargetPresentation : HudUnitPresentation.OvertipOnly;
        destination.Add(HudEvent.UnitChanged(
            stamp,
            entityId,
            authority.Name,
            authority.Health,
            authority.MaximumHealth,
            presentation));
    }

    private void ApplyMappedUnitChanges(IEnumerable<HudEvent> items)
    {
        foreach (HudEvent item in items)
        {
            if (item.Kind == HudEventKind.UnitChanged)
            {
                ApplyReliableAuthority(item, removed: false);
            }
        }
    }

    private static HudItemStack MapItem(ItemStackState item, string owner)
    {
        if (item.InstanceId == 0 || string.IsNullOrWhiteSpace(item.ProductItemId) ||
            item.StackCount == 0 || item.StackCount > int.MaxValue || item.CounterValue < 0 ||
            (item.HasRuneProductResourceId && string.IsNullOrWhiteSpace(item.RuneProductResourceId)) ||
            (item.HasRuneSlotProductResourceId && string.IsNullOrWhiteSpace(item.RuneSlotProductResourceId)))
        {
            throw new ArgumentException($"{owner} carries an invalid item stack.");
        }

        return new HudItemStack(
            new HudId(item.ProductItemId),
            checked((int)item.StackCount),
            item.InstanceId,
            item.CounterValue,
            item.IsBound,
            item.IsCursed,
            item.IsQuestOperator,
            item.RemoveTime,
            item.HasRuneProductResourceId ? new HudId(item.RuneProductResourceId) : HudId.Empty,
            item.HasRuneSlotProductResourceId ? new HudId(item.RuneSlotProductResourceId) : HudId.Empty);
    }

    private static HudInventoryCooldown MapCooldown(ItemSlotSpellCooldownState cooldown, string owner)
    {
        if (string.IsNullOrWhiteSpace(cooldown.ProductSpellId))
        {
            throw new ArgumentException($"{owner} cooldown has no product spell identifier.");
        }

        int remaining = CheckedMilliseconds(cooldown.RemainingMilliseconds, $"{owner} cooldown remaining");
        int duration = CheckedMilliseconds(cooldown.DurationMilliseconds, $"{owner} cooldown duration");
        if (remaining <= 0 || remaining > duration)
        {
            throw new ArgumentException($"{owner} cooldown has an invalid duration.");
        }

        return new HudInventoryCooldown(new HudId(cooldown.ProductSpellId), remaining, duration);
    }

    private static HudQuestDocument MapQuestLogDocument(QuestLogEntry entry)
    {
        ValidateDefinedEnum(entry.State, $"Quest {entry.QuestId} state");
        HudQuestClientState state = entry.State switch
        {
            QuestUiState.InProgress => HudQuestClientState.InProgress,
            QuestUiState.ReadyToReturn => HudQuestClientState.Completable,
            QuestUiState.Completed => HudQuestClientState.TurnedIn,
            QuestUiState.Failed => HudQuestClientState.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(entry.State)),
        };
        return new HudQuestDocument(
            new HudId(entry.QuestId),
            ProductText(entry.Name, entry.QuestId, "title"),
            new HudId($"{entry.QuestId}.description"),
            state,
            state is HudQuestClientState.InProgress or HudQuestClientState.Completable,
            MapQuestObjectives(entry.Objectives, entry.QuestId, HudProduct.QuestLogObjectiveCount));
    }

    private static HudQuestDocument MapQuestInfoDocument(
        QuestInfo info,
        QuestProgress progress,
        HudQuestClientState state,
        HudQuestRewardSnapshot reward)
    {
        string description = state == HudQuestClientState.Offered ? info.StartText : info.FinishText;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = info.Goal;
        }

        return new HudQuestDocument(
            new HudId(info.Id),
            ProductText(info.Name, info.Id, "title"),
            ProductText(description, info.Id, "description"),
            state,
            info.CanCancel,
            MapQuestObjectives(progress.Objectives, info.Id, HudProduct.QuestInfoObjectiveCount),
            reward);
    }

    private static HudQuestObjective[] MapQuestObjectives(
        IEnumerable<QuestObjectiveState> objectives,
        string questId,
        int maximumCount)
    {
        QuestObjectiveState[] values = objectives.ToArray();
        if (values.Length > maximumCount)
        {
            throw new ArgumentException($"Quest {questId} exceeds its authored objective pool.");
        }

        var mapped = new HudQuestObjective[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            QuestObjectiveState objective = values[index];
            ValidateDefinedEnum(objective.Type, $"Quest {questId} objective {index} type");
            if (objective.Progress < 0 || objective.Required <= 0 || objective.Progress > objective.Required ||
                objective.Progress > int.MaxValue || objective.Required > int.MaxValue)
            {
                throw new ArgumentException($"Quest {questId} objective {index} has invalid progress.");
            }

            foreach (QuestObjectiveItem item in objective.Items)
            {
                if (string.IsNullOrWhiteSpace(item.ProductItemId) || item.Amount <= 0)
                {
                    throw new ArgumentException($"Quest {questId} objective {index} has an invalid item requirement.");
                }
            }

            mapped[index] = new HudQuestObjective(
                checked((uint)index),
                ProductText(objective.Name, questId, $"objective.{index}"),
                checked((int)objective.Progress),
                checked((int)objective.Required),
                objective.ShowCounterValue);
        }

        return mapped;
    }

    private static HudQuestRewardSnapshot MapQuestReward(QuestReward reward)
    {
        if (reward.Experience < 0 || reward.Honor < 0 || reward.Money < 0 ||
            reward.MandatoryItems.Count > HudProduct.QuestInfoRewardItemCount ||
            reward.AlternativeItems.Count > HudProduct.QuestInfoRewardItemCount ||
            reward.Reputations.Count > HudProduct.QuestInfoReputationCount ||
            reward.Currencies.Count > HudProduct.QuestInfoCurrencyCount ||
            reward.MandatoryItemsCount < reward.MandatoryItems.Count)
        {
            throw new ArgumentException("Quest reward exceeds an authored collection or carries a negative amount.");
        }

        return new HudQuestRewardSnapshot(
            reward.Experience,
            reward.Honor,
            reward.Money,
            reward.MandatoryItems.Select(MapRewardItem).ToArray(),
            reward.AlternativeItems.Select(MapRewardItem).ToArray(),
            reward.Reputations.Select(item =>
            {
                if (string.IsNullOrWhiteSpace(item.Faction) || item.Value < 0)
                {
                    throw new ArgumentException("Quest reputation reward is invalid.");
                }

                return new HudQuestReputation(new HudId(item.Faction), item.Value);
            }).ToArray(),
            reward.Currencies.Select(item =>
            {
                if (string.IsNullOrWhiteSpace(item.CurrencyId) || item.Value < 0)
                {
                    throw new ArgumentException("Quest currency reward is invalid.");
                }

                return new HudQuestCurrency(new HudId(item.CurrencyId), item.Value);
            }).ToArray());
    }

    private static HudRewardItem MapRewardItem(QuestRewardItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ProductItemId) || item.Count == 0 || item.Count > int.MaxValue)
        {
            throw new ArgumentException("Quest item reward is invalid.");
        }

        return new HudRewardItem(new HudId(item.ProductItemId), checked((int)item.Count));
    }

    private static void ValidateQuestIdentity(QuestInfoReplacement replacement)
    {
        string requested = replacement.RequestedQuestId;
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(replacement.Info.Id) ||
            string.IsNullOrWhiteSpace(replacement.Progress.Id) ||
            !StringComparer.Ordinal.Equals(requested, replacement.Info.Id) ||
            !StringComparer.Ordinal.Equals(requested, replacement.Progress.Id))
        {
            throw new ArgumentException("Quest-info identifiers do not name one product quest.");
        }
    }

    private static int MapEquipmentSlot(EquipmentSlotId slot) => slot switch
    {
        EquipmentSlotId.Mainhand => 0,
        EquipmentSlotId.Offhand => 1,
        EquipmentSlotId.Ranged => 2,
        EquipmentSlotId.Helm => 3,
        EquipmentSlotId.Mantle => 4,
        EquipmentSlotId.Cloak => 5,
        EquipmentSlotId.Armor => 6,
        EquipmentSlotId.Gloves => 7,
        EquipmentSlotId.Belt => 8,
        EquipmentSlotId.Pants => 9,
        EquipmentSlotId.Boots => 10,
        EquipmentSlotId.Earrings => 11,
        EquipmentSlotId.Necklace => 12,
        EquipmentSlotId.Tabard => 13,
        EquipmentSlotId.Shirt => 14,
        EquipmentSlotId.Bracers => 15,
        EquipmentSlotId.Ring1 => 16,
        EquipmentSlotId.Ring2 => 17,
        EquipmentSlotId.Trinket => 18,
        EquipmentSlotId.DeathInsurance => 20,
        _ => throw new ArgumentException($"Character equipment slot {slot} is not an authored UI role."),
    };

    private static HudTargetSelectionRefusal MapTargetRefusal(TargetSelectRefusal refusal) => refusal switch
    {
        TargetSelectRefusal.None => HudTargetSelectionRefusal.None,
        TargetSelectRefusal.NoTarget => HudTargetSelectionRefusal.NoTarget,
        TargetSelectRefusal.InvalidTarget => HudTargetSelectionRefusal.InvalidTarget,
        TargetSelectRefusal.TargetDead => HudTargetSelectionRefusal.TargetDead,
        _ => throw new ArgumentException($"Unsupported target refusal {refusal}."),
    };

    private static HudLootRefusal MapLootRefusal(LootUiRefusal refusal) => refusal switch
    {
        LootUiRefusal.None => HudLootRefusal.None,
        LootUiRefusal.BagFull => HudLootRefusal.BagFull,
        LootUiRefusal.NotYourLoot => HudLootRefusal.NotOwner,
        LootUiRefusal.NoCorpse => HudLootRefusal.OutOfRange,
        LootUiRefusal.AlreadyLooted or LootUiRefusal.InProgress or LootUiRefusal.Internal or
            LootUiRefusal.InvalidIndex => HudLootRefusal.Unavailable,
        _ => throw new ArgumentException($"Unsupported loot refusal {refusal}."),
    };

    private static HudQuestRefusal MapQuestRefusal(QuestInfoRefusal refusal) => refusal switch
    {
        QuestInfoRefusal.None => HudQuestRefusal.None,
        QuestInfoRefusal.UnknownQuest or QuestInfoRefusal.Unavailable => HudQuestRefusal.Unavailable,
        QuestInfoRefusal.LogFull => HudQuestRefusal.LogFull,
        QuestInfoRefusal.OutOfRange => HudQuestRefusal.OutOfRange,
        QuestInfoRefusal.WrongNpc => HudQuestRefusal.WrongNpc,
        QuestInfoRefusal.NotComplete or QuestInfoRefusal.CannotCancel or QuestInfoRefusal.StaleRevision or
            QuestInfoRefusal.InvalidRewardChoice => HudQuestRefusal.InvalidState,
        QuestInfoRefusal.AlreadyComplete => HudQuestRefusal.AlreadyFinished,
        QuestInfoRefusal.BagFull => HudQuestRefusal.BagFull,
        QuestInfoRefusal.Internal => HudQuestRefusal.System,
        _ => throw new ArgumentException($"Unsupported quest refusal {refusal}."),
    };

    private static HudId ProductText(string value, string owner, string suffix) =>
        new(string.IsNullOrWhiteSpace(value) ? $"{owner}.{suffix}" : value);

    private static int CheckedPartition(uint value) => CheckedInt(value, "Inventory partition size");

    private static int CheckedInt(uint value, string owner)
    {
        if (value > int.MaxValue)
        {
            throw new OverflowException($"{owner} exceeds the HUD integer range.");
        }

        return checked((int)value);
    }

    private static int CheckedMilliseconds(long value, string owner)
    {
        if (value < 0 || value > int.MaxValue)
        {
            throw new OverflowException($"{owner} is outside the HUD millisecond range.");
        }

        return checked((int)value);
    }

    private static void ValidateRevision(ulong revision, string owner)
    {
        if (revision == 0)
        {
            throw new ArgumentException($"{owner} has no authority revision.");
        }
    }

    private static void ValidateDefinedRefusal<T>(T value, T unspecified, string owner)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value) || EqualityComparer<T>.Default.Equals(value, unspecified))
        {
            throw new ArgumentException($"{owner} carries unsupported refusal {value}.");
        }
    }

    private static void ValidateDefinedEnum<T>(T value, string owner)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException($"{owner} carries unsupported value {value}.");
        }
    }

    private bool TryTakeNextEvent(out HudEvent item)
    {
        if (_reliableEvents.Count == 0 && _snapshotEvents.Count == 0)
        {
            item = default;
            return false;
        }

        ulong snapshotKey = 0;
        HudEvent snapshot = default;
        bool hasSnapshot = false;
        foreach ((ulong key, HudEvent candidate) in _snapshotEvents)
        {
            if (!hasSnapshot || candidate.Stamp.CompareTo(snapshot.Stamp) < 0)
            {
                snapshotKey = key;
                snapshot = candidate;
                hasSnapshot = true;
            }
        }

        if (_reliableEvents.Count > 0 &&
            (!hasSnapshot || _reliableEvents.Peek().Stamp.CompareTo(snapshot.Stamp) <= 0))
        {
            item = _reliableEvents.Dequeue();
            return true;
        }

        item = snapshot;
        _snapshotEvents.Remove(snapshotKey);
        return true;
    }

    private bool TryEnqueueReliable(HudEvent item)
    {
        if (_reliableEvents.Count >= _options.ReliableEventCapacity)
        {
            Fail(
                SessionHudFaultCode.ReliableEventQueueFull,
                $"Reliable HUD event queue reached {_options.ReliableEventCapacity} entries.");
            return false;
        }

        _reliableEvents.Enqueue(item);
        return true;
    }

    private bool TryEnqueueReliable(IReadOnlyCollection<HudEvent> items)
    {
        if (_reliableEvents.Count + items.Count > _options.ReliableEventCapacity)
        {
            Fail(
                SessionHudFaultCode.ReliableEventQueueFull,
                $"Reliable HUD event queue cannot atomically accept {items.Count} entries at capacity {_options.ReliableEventCapacity}.");
            return false;
        }

        foreach (HudEvent item in items)
        {
            _reliableEvents.Enqueue(item);
        }

        return true;
    }

    private void ApplyReliableAuthority(HudEvent item, bool removed)
    {
        if (!_unitAuthorities.TryGetValue(item.EntityId, out UnitAuthority authority) ||
            authority.Stamp.CompareTo(item.Stamp) <= 0)
        {
            _unitAuthorities[item.EntityId] = removed
                ? new UnitAuthority(
                    item.Stamp,
                    authority.Name,
                    authority.Health,
                    authority.MaximumHealth,
                    authority.Presentation,
                    true)
                : UnitAuthority.From(item, false);
        }
    }

    private bool EnsureAuthorityCapacity(ulong entityId)
    {
        if (_unitAuthorities.ContainsKey(entityId) || _unitAuthorities.Count < _options.SnapshotEntityCapacity)
        {
            return true;
        }

        Fail(
            SessionHudFaultCode.SnapshotEntityCapacityExceeded,
            $"HUD entity authority table reached {_options.SnapshotEntityCapacity} entries.");
        return false;
    }

    private bool TryNextStamp(ulong revision, out HudStamp stamp)
    {
        if (_nextOrdinal > uint.MaxValue)
        {
            stamp = default;
            Fail(SessionHudFaultCode.AuthorityOrdinalExhausted, "HUD authority ordinal exhausted for this connection epoch.");
            return false;
        }

        stamp = new HudStamp(_sourceEpoch, revision, checked((uint)_nextOrdinal));
        _nextOrdinal++;
        return true;
    }

    private SessionHudObservation Fail(SessionHudFaultCode code, string detail)
    {
        if (_state == HudSessionState.Open)
        {
            _state = HudSessionState.Faulted;
            _fault = new SessionHudFault(code, detail);
            _commands.Clear();
        }

        return SessionHudObservation.Terminal;
    }

    private void CountDroppedSnapshot()
    {
        if (_droppedSnapshots < int.MaxValue)
        {
            _droppedSnapshots++;
        }
    }

    private bool IsSupported(in HudCommand command) => command.Kind switch
    {
        HudCommandKind.ActivateAction => (uint)command.Slot < ActionSlotCount && ValidExpectedRevision(command),
        HudCommandKind.SelectWorldEntity => true,
        HudCommandKind.SubmitChat => command.ChatSubmission is not null,
        HudCommandKind.InteractWorldEntity => command.EntityId != 0,
        HudCommandKind.MoveInventoryItem =>
            (uint)command.Slot < HudProduct.InventorySlotCount &&
            (uint)command.Auxiliary < HudProduct.InventorySlotCount &&
            command.Slot != command.Auxiliary && ValidExpectedRevision(command),
        HudCommandKind.DropInventoryItem =>
            (uint)command.Slot < HudProduct.InventorySlotCount && command.Count > 0 && ValidExpectedRevision(command),
        HudCommandKind.UseInventoryItem or HudCommandKind.DressInventoryItem =>
            (uint)command.Slot < HudProduct.InventorySlotCount && ValidExpectedRevision(command),
        HudCommandKind.UndressInventoryItem =>
            (uint)command.Slot < CharacterEquipmentCount && ValidExpectedRevision(command),
        HudCommandKind.TakeLootItem =>
            command.EntityId != 0 && (uint)command.Slot < LootItemCount && ValidExpectedRevision(command),
        HudCommandKind.TakeLootMoney =>
            command.EntityId != 0 && command.Amount == -1 && ValidExpectedRevision(command),
        HudCommandKind.TakeAllLoot => command.EntityId != 0 && ValidExpectedRevision(command),
        HudCommandKind.CloseLoot => true,
        HudCommandKind.AbandonQuest or HudCommandKind.ShareQuest =>
            !command.Value.IsEmpty && ValidExpectedRevision(command),
        HudCommandKind.AcceptSharedQuest or HudCommandKind.DeclineSharedQuest =>
            !command.Value.IsEmpty && !command.SecondaryValue.IsEmpty && ValidExpectedRevision(command),
        HudCommandKind.AcceptQuest =>
            command.EntityId != 0 && !command.Value.IsEmpty && ValidExpectedRevision(command),
        HudCommandKind.TurnInQuest =>
            command.EntityId != 0 && !command.Value.IsEmpty && command.Slot is >= -1 and < 5 &&
            ValidExpectedRevision(command),
        _ => false,
    };

    private bool ValidExpectedRevision(in HudCommand command) =>
        command.ExpectedRevision.SourceEpoch == _sourceEpoch && command.ExpectedRevision.Revision != 0;

    private static ulong ResolveSnapshotRevision(ulong envelopeRevision, ulong batchRevision)
    {
        if (envelopeRevision != 0 && batchRevision != 0 && envelopeRevision != batchRevision)
        {
            throw new ArgumentException(
                $"Snapshot authority mismatch: envelope {envelopeRevision}, batch {batchRevision}.");
        }

        return batchRevision != 0 ? batchRevision : envelopeRevision;
    }

    private static void ValidateEntity(EntitySnapshot entity)
    {
        if (entity.EntityId == 0)
        {
            throw new ArgumentException("Entity snapshot has no identifier.");
        }

        if (string.IsNullOrWhiteSpace(entity.NameKey) && string.IsNullOrWhiteSpace(entity.ContentId))
        {
            throw new ArgumentException($"Entity {entity.EntityId} has neither a name key nor content identifier.");
        }
    }

    private void TrackEntityKind(EntitySnapshot entity)
    {
        if (entity.Kind == EntityKind.Player)
        {
            _playerEntityIds.Add(entity.EntityId);
        }
        else
        {
            _playerEntityIds.Remove(entity.EntityId);
        }
    }

    private HudEvent MapUnit(EntitySnapshot entity, HudStamp stamp)
    {
        HudUnitPresentation presentation = entity.EntityId == _ownEntityId
            ? AvatarPresentation
            : entity.EntityId == _selectedTargetEntityId ? TargetPresentation : HudUnitPresentation.OvertipOnly;
        return HudEvent.UnitChanged(
            stamp,
            entity.EntityId,
            new HudId(string.IsNullOrWhiteSpace(entity.NameKey) ? entity.ContentId : entity.NameKey),
            entity.Health,
            entity.MaxHealth,
            presentation);
    }

    private static HudId EventId(
        string family,
        HudStamp stamp,
        ulong firstEntity,
        ulong secondEntity,
        string value) => new(string.Create(
            CultureInfo.InvariantCulture,
            $"session.{family}.{stamp.SourceEpoch}.{stamp.Revision}.{stamp.Ordinal}.{firstEntity}.{secondEntity}.{value}"));

    private readonly record struct UnitAuthority(
        HudStamp Stamp,
        HudId Name,
        int Health,
        int MaximumHealth,
        HudUnitPresentation Presentation,
        bool Removed)
    {
        public static UnitAuthority From(HudEvent item, bool removed) =>
            new(item.Stamp, item.ContentId, item.Value, item.Auxiliary, item.UnitPresentation, removed);
    }
}
