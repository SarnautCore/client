using System.Globalization;
using Sarnaut.Protocol.V1;
using SarnautCore.NativeHud;

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
    private static readonly HudSessionCapabilities SupportedCapabilities = new(
        HudEventFamilies.Units | HudEventFamilies.CombatFeedback | HudEventFamilies.QuestTracker,
        HudCommandFamilies.ActivateAction |
        HudCommandFamilies.SelectWorldEntity |
        HudCommandFamilies.InteractWorldEntity);

    private readonly object _gate = new();
    private readonly uint _sourceEpoch;
    private ulong _ownEntityId;
    private readonly SessionHudAdapterOptions _options;
    private readonly Queue<HudEvent> _reliableEvents = new();
    private readonly Dictionary<ulong, HudEvent> _snapshotEvents = [];
    private readonly Dictionary<ulong, UnitAuthority> _unitAuthorities = [];
    private readonly Queue<HudCommand> _commands = new();
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
                combat.TargetMaxHealth);
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

        HudEvent item = HudEvent.UnitChanged(stamp, entityId, authority.Name, 0, authority.MaximumHealth);
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
                ? new UnitAuthority(item.Stamp, authority.Name, authority.Health, authority.MaximumHealth, true)
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

    private static bool IsSupported(in HudCommand command) => command.Kind switch
    {
        HudCommandKind.ActivateAction => command.Slot >= 0 && !command.Value.IsEmpty,
        HudCommandKind.SelectWorldEntity => command.EntityId != 0,
        HudCommandKind.InteractWorldEntity => command.EntityId != 0,
        _ => false,
    };

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

    private static HudEvent MapUnit(EntitySnapshot entity, HudStamp stamp) => HudEvent.UnitChanged(
        stamp,
        entity.EntityId,
        new HudId(string.IsNullOrWhiteSpace(entity.NameKey) ? entity.ContentId : entity.NameKey),
        entity.Health,
        entity.MaxHealth);

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
        bool Removed)
    {
        public static UnitAuthority From(HudEvent item, bool removed) =>
            new(item.Stamp, item.ContentId, item.Value, item.Auxiliary, removed);
    }
}
