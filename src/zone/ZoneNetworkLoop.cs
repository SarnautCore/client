using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Godot;
using Sarnaut.Protocol.V1;
using SarnautCore.NativeHud;
using SarnautCore.Networking;
using SarnautCore.Shell;

namespace SarnautCore;

public partial class ZoneNetworkLoop : Node
{
    private const double SendIntervalSeconds = 1.0 / 20.0;
    private const double StatusIntervalSeconds = 0.25;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<SnapshotBatch> _receivedSnapshots = new();
    private readonly ConcurrentQueue<ServerMessage> _receivedMessages = new();
    private readonly ConcurrentQueue<ConnectionUpdate> _connectionUpdates = new();
    private readonly Channel<ClientMoveIntent> _moveIntents = Channel.CreateBounded<ClientMoveIntent>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly Channel<ClientMessage> _commands = Channel.CreateUnbounded<ClientMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SnapshotTimeline _timeline = new();
    private readonly MoveIntentCadence _cadence = new(SendIntervalSeconds);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private WalkaboutController _walker = null!;
    private ZoneEntityVisualFactory _visuals = null!;
    private ZoneEntityPicker _picker = null!;
    private Action<string> _setStatus = null!;
    private Action? _onAdmitted;
    private Action<string>? _onRefused;
    private SessionHudAdapter? _hudSession;
    private Task? _networkTask;
    private ulong _ownEntityId;
    private double _statusAccumulator;
    private bool _connected;
    private ulong _nextHudRequestId = 1;

    [Export(PropertyHint.Range, "0.1,0.15,0.005")]
    public double InterpolationDelaySeconds { get; set; } = 0.125;

    /// <summary>How far a Tab press will reach for a target.</summary>
    [Export(PropertyHint.Range, "5,120,1")]
    public float TargetRangeMetres { get; set; } = 45.0f;

    /// <summary>
    /// Every entity the shard is replicating to this client, by id and by the
    /// body a pick ray hits.
    /// </summary>
    public EntityRegistry Entities { get; private set; } = null!;

    /// <summary>The entity this client is playing.</summary>
    public ulong OwnEntityId => _ownEntityId;

    /// <summary>The unmodified canonical spawn returned by EnterZoneResponse.</summary>
    public Vec3? EnteredSpawnPosition { get; private set; }

    /// <summary>The entity the player has selected, or 0 for none.</summary>
    public ulong TargetEntityId { get; private set; }

    /// <summary>Raised when the selection changes, with 0 for a cleared target.</summary>
    public event Action<ulong>? TargetChanged;

    /// <summary>The attached native HUD session port.</summary>
    public IHudSession HudSession => _hudSession ??
        throw new InvalidOperationException("No native HUD session adapter is attached.");

    /// <summary>
    /// Attaches the one adapter that observes this loop's session. Call this before Start so
    /// no authoritative message can arrive before the subscription exists.
    /// </summary>
    public void AttachHudSession(SessionHudAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (_networkTask is not null)
        {
            throw new InvalidOperationException("The HUD session adapter must be attached before the network loop starts.");
        }

        if (_hudSession is not null)
        {
            throw new InvalidOperationException("A HUD session adapter is already attached.");
        }

        _hudSession = adapter;
    }

    /// <summary>Queues one protocol-neutral HUD command for this session loop.</summary>
    public bool TryEnqueueHudCommand(in HudCommand command) => _hudSession?.TryWrite(command) == true;

    /// <param name="ticket">
    /// The opaque single-use shard ticket the account service minted for this
    /// character (ADR 0030). It travels in <c>EnterZoneRequest</c> and the shard
    /// burns it on redemption; nothing here keeps or prints a copy.
    /// </param>
    public void Start(
        WalkaboutController walker,
        Node3D entityRoot,
        EntityModelCatalog catalog,
        string address,
        string zoneId,
        string contentPackId,
        Secret ticket,
        Action<string> setStatus,
        Action? onAdmitted = null,
        Action<string>? onRefused = null)
    {
        _walker = walker;
        _visuals = new ZoneEntityVisualFactory(entityRoot, catalog);
        Entities = new EntityRegistry(_visuals);
        _picker = new ZoneEntityPicker();
        AddChild(_picker);
        _setStatus = setStatus;
        _onAdmitted = onAdmitted;
        _onRefused = onRefused;
        _walker.NetworkControlled = true;
        _setStatus($"Online: connecting to {address}...");
        _networkTask = RunNetworkAsync(address, zoneId, contentPackId, ticket, _shutdown.Token);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_connected)
        {
            return;
        }

        _cadence.Accumulate(delta);
        Vector2 direction = OnlineCoordinateFrame.ToServerGround(_walker.NetworkMoveDirection);
        while (_cadence.TryDequeue(direction.X, direction.Y, _walker.Rotation.Y, out ClientMoveIntent intent))
        {
            _moveIntents.Writer.TryWrite(intent);
        }
    }

    public override void _Process(double delta)
    {
        DrainConnectionUpdates();
        DrainHudCommands();
        while (_receivedMessages.TryDequeue(out ServerMessage? message))
        {
            ApplyServerMessage(message);
        }

        while (_receivedSnapshots.TryDequeue(out SnapshotBatch? snapshot))
        {
            _timeline.Add(snapshot, _clock.Elapsed.TotalSeconds);
        }

        if (!_connected || _timeline.LatestServerTick == 0)
        {
            return;
        }

        UpdateEntities();
        _statusAccumulator += delta;
        if (_statusAccumulator >= StatusIntervalSeconds)
        {
            _statusAccumulator = 0;
            _setStatus(
                $"Online: QUIC stream | entity {_ownEntityId} | tick {_timeline.LatestServerTick} | " +
                $"position {_walker.Position.X:F1}, {_walker.Position.Z:F1} | " +
                $"{_timeline.LatestEntityIds.Count} entities " +
                $"({_visuals.ModelCount} models, {_visuals.CapsuleCount} capsules)" +
                (TargetEntityId == 0 ? string.Empty : $" | target {TargetEntityId}"));
        }
    }

    public override void _ExitTree()
    {
        _connected = false;
        _walker.NetworkControlled = false;
        _shutdown.Cancel();
        _moveIntents.Writer.TryComplete();
        _commands.Writer.TryComplete();
        _hudSession?.Close();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Selects the entity under a point on screen, and returns false when the
    /// ray hit nothing the registry knows.
    /// </summary>
    public bool TryTargetAtScreenPoint(Vector2 screenPoint, out ulong entityId)
    {
        if (!TryPickEntityAtScreenPoint(screenPoint, out entityId))
        {
            // Clicking past everything is how a target is dropped.
            SetTarget(0);
            return false;
        }

        SetTarget(entityId);
        return true;
    }

    /// <summary>
    /// Reports the replicated entity under a screen point without changing selection.
    /// Native HUD input uses this only after its pixel-aware role hit test misses.
    /// </summary>
    public bool TryPickEntityAtScreenPoint(Vector2 screenPoint, out ulong entityId)
    {
        entityId = 0;
        if (Entities is null
            || !_picker.TryPick(GetViewport().GetCamera3D(), screenPoint, out ulong pickKey)
            || !Entities.TryGetByPickKey(pickKey, out TrackedEntity? hit))
        {
            return false;
        }

        entityId = hit.EntityId;
        return true;
    }

    /// <summary>Steps the Tab cycle outwards from the player.</summary>
    public bool TryCycleTarget(out ulong entityId)
    {
        entityId = 0;
        if (Entities is null || !Entities.HasLocalSample)
        {
            return false;
        }

        SampledEntity local = Entities.LocalSample;
        if (!Entities.TryCycleTarget(TargetEntityId, local.X, local.Y, local.Z, TargetRangeMetres, out entityId))
        {
            SetTarget(0);
            return false;
        }

        SetTarget(entityId);
        return true;
    }

    public void SetTarget(ulong entityId)
    {
        if (Entities is null || TargetEntityId == entityId)
        {
            return;
        }

        if (TargetEntityId != 0 && Entities.TryGet(TargetEntityId, out TrackedEntity? previous)
            && previous.Visual is NetworkEntityVisual previousVisual)
        {
            previousVisual.Targeted = false;
        }

        TargetEntityId = entityId;
        if (entityId != 0 && Entities.TryGet(entityId, out TrackedEntity? current))
        {
            if (current.Visual is NetworkEntityVisual currentVisual)
            {
                currentVisual.Targeted = true;
            }
        }

        TargetChanged?.Invoke(entityId);
    }

    public void RequestInteract(ulong targetEntityId)
    {
        if (targetEntityId != 0)
        {
            EnqueueCommand(new ClientMessage { Interact = new Interact { TargetEntityId = targetEntityId } });
        }
    }

    private async Task RunNetworkAsync(
        string address,
        string zoneId,
        string contentPackId,
        Secret ticket,
        CancellationToken cancellationToken)
    {
        try
        {
            GameEndpoint endpoint = GameEndpoint.Parse(address);
            await using GameSession session = await GameSession.ConnectAsync(
                endpoint,
                zoneId,
                "godot-sar20",
                allowUntrustedDevelopmentCertificate: true,
                packId: contentPackId,
                ticket: ticket.Reveal(),
                cancellationToken: cancellationToken);

            _hudSession?.BindOwnEntity(session.EnteredZone.OwnEntityId);

            _connectionUpdates.Enqueue(ConnectionUpdate.Ready(
                session.EnteredZone.OwnEntityId,
                session.EnteredZone.SpawnPosition ?? new Sarnaut.Protocol.V1.Vec3(),
                session.ServerHello.BuildId));
            try
            {
                await Task.WhenAll(
                    ReceiveMessagesAsync(session, cancellationToken),
                    SendMovementAsync(session, cancellationToken),
                    SendCommandsAsync(session, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Leaving the zone is a clean logout, so the shard's save
                // checkpoint runs ahead of the disconnect rather than racing it.
                await SendLogoutQuietlyAsync(session);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _hudSession?.ReportTransportFault(exception.Message);
            _connectionUpdates.Enqueue(ConnectionUpdate.Failed(exception.Message));
        }
    }

    private static async Task SendLogoutQuietlyAsync(GameSession session)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await session.SendLogoutAsync(deadline.Token);
        }
        catch (Exception exception)
        {
            // The connection is going away either way; a failed goodbye is not
            // worth a second failure path.
            GD.PushWarning($"Zone logout was not delivered: {exception.Message}");
        }
    }

    private async Task ReceiveMessagesAsync(GameSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ServerMessage message = await session.ReadAsync(cancellationToken);
            SessionHudObservation observation = _hudSession?.Observe(message) ?? SessionHudObservation.NotSubscribed;
            if (observation == SessionHudObservation.Terminal && _hudSession?.State == HudSessionState.Faulted)
            {
                throw new InvalidDataException(
                    $"Native HUD session adapter faulted: {_hudSession.Fault?.Code} {_hudSession.Fault?.Detail}".TrimEnd());
            }

            if (message.PayloadCase == ServerMessage.PayloadOneofCase.SnapshotBatch)
            {
                _receivedSnapshots.Enqueue(message.SnapshotBatch);
            }
            else
            {
                _receivedMessages.Enqueue(message);
            }
        }
    }

    private async Task SendMovementAsync(GameSession session, CancellationToken cancellationToken)
    {
        await foreach (ClientMoveIntent intent in _moveIntents.Reader.ReadAllAsync(cancellationToken))
        {
            await session.SendMoveIntentAsync(intent, cancellationToken);
        }
    }

    private async Task SendCommandsAsync(GameSession session, CancellationToken cancellationToken)
    {
        await foreach (ClientMessage command in _commands.Reader.ReadAllAsync(cancellationToken))
        {
            await session.SendAsync(command, cancellationToken);
        }
    }

    private void DrainConnectionUpdates()
    {
        while (_connectionUpdates.TryDequeue(out ConnectionUpdate? update))
        {
            if (update is null)
            {
                continue;
            }

            if (update.Error is not null)
            {
                _connected = false;
                _walker.NetworkControlled = false;
                _setStatus($"Online connection failed: {update.Error}");
                GD.PushError($"Zone network connection failed: {update.Error}");
                _onRefused?.Invoke(update.Error);
                continue;
            }

            _ownEntityId = update.OwnEntityId;
            EnteredSpawnPosition = update.SpawnPosition!.Clone();
            // The response's spawn is authoritative: it is the server's answer,
            // not a confirmation of a client request (session spec rule 5.4.6).
            _walker.ApplyAuthoritativePosition(OnlineCoordinateFrame.ToGodot(EnteredSpawnPosition));
            _connected = true;
            _setStatus($"Online: joined as entity {_ownEntityId} | server {update.ServerBuildId} | QUIC stream");
            _onAdmitted?.Invoke();
        }
    }

    /// <summary>
    /// Draws one frame of the replicated world.
    /// </summary>
    /// <remarks>
    /// This used to be quadratic in the entity count: it read a freshly
    /// allocated id array twice, searched the timeline and then linearly scanned
    /// both batches once per entity, and swept for stale entities with a
    /// <c>Contains</c> inside a <c>Where</c>. The window is now opened once and
    /// the registry does the rest in one pass; see
    /// <c>tools/SarnautCore.EntityBench/RESULTS.md</c>.
    /// </remarks>
    private void UpdateEntities()
    {
        SnapshotWindow window = _timeline.OpenWindow(_clock.Elapsed.TotalSeconds, InterpolationDelaySeconds);
        Entities.Reconcile(window, _ownEntityId);
        if (Entities.HasLocalSample)
        {
            SampledEntity local = Entities.LocalSample;
            _walker.ApplyAuthoritativePosition(OnlineCoordinateFrame.ToGodot(local));
        }

        if (TargetEntityId != 0 && !Entities.Contains(TargetEntityId))
        {
            // The shard stopped replicating the target: it died and despawned,
            // or it left the subscription. Either way the selection is gone.
            SetTarget(0);
        }
    }

    private void ApplyServerMessage(ServerMessage message)
    {
        switch (message.PayloadCase)
        {
            case ServerMessage.PayloadOneofCase.SpawnEvent:
                if (message.SpawnEvent.Entity is not null)
                {
                    Entities.Spawn(message.SpawnEvent.Entity, _ownEntityId);
                }

                break;
            case ServerMessage.PayloadOneofCase.DespawnEvent:
                Entities.Remove(message.DespawnEvent.EntityId);
                if (TargetEntityId == message.DespawnEvent.EntityId)
                {
                    SetTarget(0);
                }

                break;
            case ServerMessage.PayloadOneofCase.CombatEvent:
                CombatEvent combat = message.CombatEvent;
                if (combat.CasterId == _ownEntityId)
                {
                    _walker.PlayAttack();
                }
                else if (Entities.TryGet(combat.CasterId, out TrackedEntity? caster)
                    && caster.Visual is NetworkEntityVisual casterVisual)
                {
                    casterVisual.PlayAttack();
                }

                if (!combat.KillingBlow
                    && combat.Rejection == AbilityRejection.None
                    && Entities.TryGet(combat.TargetId, out TrackedEntity? target)
                    && target.Visual is NetworkEntityVisual targetVisual)
                {
                    targetVisual.PlayHit();
                }

                break;
            case ServerMessage.PayloadOneofCase.DeathEvent:
                if (message.DeathEvent.VictimEntityId == _ownEntityId)
                {
                    _walker.PlayDeath();
                }
                else if (Entities.TryGet(message.DeathEvent.VictimEntityId, out TrackedEntity? victim)
                    && victim.Visual is NetworkEntityVisual victimVisual)
                {
                    victimVisual.PlayDeath();
                }

                break;
        }
    }

    private void EnqueueCommand(ClientMessage message)
    {
        if (_connected)
        {
            _commands.Writer.TryWrite(message);
        }
    }

    private void DrainHudCommands()
    {
        if (!_connected || _hudSession is null)
        {
            return;
        }

        while (_hudSession.TryTakeCommand(out HudCommand command))
        {
            switch (command.Kind)
            {
                case HudCommandKind.SelectWorldEntity:
                    SetTarget(command.EntityId);
                    if (!TryNextHudRequestId(out ulong targetRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        TargetSelect = new TargetSelect
                        {
                            TargetEntityId = command.EntityId,
                            RequestId = targetRequestId,
                        },
                    });
                    break;
                case HudCommandKind.InteractWorldEntity:
                    RequestInteract(command.EntityId);
                    break;
                case HudCommandKind.ActivateAction:
                    if (!TryExpectedRevision(command, out ulong actionRevision)
                        || !TrySlot(command, command.Slot, 36, "action-bar", out uint actionSlot)
                        || !TryNextHudRequestId(out ulong actionRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        ActivateAction = new ActivateAction
                        {
                            RequestId = actionRequestId,
                            SlotIndex = actionSlot,
                            ClientTick = _timeline.LatestServerTick,
                            ExpectedRevision = actionRevision,
                        },
                    });
                    break;
                case HudCommandKind.MoveInventoryItem:
                    if (command.Flag)
                    {
                        RejectHudCommand(command, "the inventory wire moves only a complete stack");
                        return;
                    }

                    if (!TryExpectedRevision(command, out ulong inventoryRevision)
                        || !TrySlot(command, command.Slot, 60, "inventory source", out uint fromSlot)
                        || !TrySlot(command, command.Auxiliary, 60, "inventory destination", out uint toSlot))
                    {
                        return;
                    }

                    if (fromSlot == toSlot)
                    {
                        RejectHudCommand(command, "inventory source and destination slots are identical");
                        return;
                    }

                    if (!TryNextHudRequestId(out ulong inventoryRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        InventoryMove = new InventoryMove
                        {
                            RequestId = inventoryRequestId,
                            ExpectedRevision = inventoryRevision,
                            FromSlot = fromSlot,
                            ToSlot = toSlot,
                        },
                    });
                    break;
                case HudCommandKind.TakeLootItem:
                    if (!TryExpectedRevision(command, out ulong lootItemRevision)
                        || !TryEntity(command, "loot item", out ulong lootItemEntity)
                        || !TrySlot(command, command.Slot, 20, "loot item", out uint lootItemIndex)
                        || !TryNextHudRequestId(out ulong lootItemRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        LootTakeItem = new LootTakeItem
                        {
                            RequestId = lootItemRequestId,
                            LootEntityId = lootItemEntity,
                            ExpectedRevision = lootItemRevision,
                            ItemIndex = checked((int)lootItemIndex),
                        },
                    });
                    break;
                case HudCommandKind.TakeLootMoney:
                    if (command.Amount != -1)
                    {
                        RejectHudCommand(command, "the loot wire supports only retail's take-all-money operation");
                        return;
                    }

                    if (!TryExpectedRevision(command, out ulong lootMoneyRevision)
                        || !TryEntity(command, "loot money", out ulong lootMoneyEntity)
                        || !TryNextHudRequestId(out ulong lootMoneyRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        LootTakeMoney = new LootTakeMoney
                        {
                            RequestId = lootMoneyRequestId,
                            LootEntityId = lootMoneyEntity,
                            ExpectedRevision = lootMoneyRevision,
                        },
                    });
                    break;
                case HudCommandKind.TakeAllLoot:
                    if (!TryExpectedRevision(command, out ulong lootAllRevision)
                        || !TryEntity(command, "loot all", out ulong lootAllEntity)
                        || !TryNextHudRequestId(out ulong lootAllRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        LootTakeAll = new LootTakeAll
                        {
                            RequestId = lootAllRequestId,
                            LootEntityId = lootAllEntity,
                            ExpectedRevision = lootAllRevision,
                        },
                    });
                    break;
                case HudCommandKind.CloseLoot:
                    if (!TryNextHudRequestId(out ulong lootCloseRequestId))
                    {
                        return;
                    }

                    // LootClose is session-contextual. The server closes the open loot
                    // context for this session; the request id only correlates its reply.
                    EnqueueCommand(new ClientMessage
                    {
                        LootClose = new LootClose { RequestId = lootCloseRequestId },
                    });
                    break;
                case HudCommandKind.AbandonQuest:
                    if (!TryQuest(command, "abandon", out string abandonQuestId, out ulong abandonRevision)
                        || !TryNextHudRequestId(out ulong abandonRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        QuestAbandon = new QuestAbandon
                        {
                            QuestId = abandonQuestId,
                            RequestId = abandonRequestId,
                            ExpectedRevision = abandonRevision,
                        },
                    });
                    break;
                case HudCommandKind.ShareQuest:
                    if (!TryQuest(command, "share", out string shareQuestId, out ulong shareRevision)
                        || !TryNextHudRequestId(out ulong shareRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        QuestShare = new QuestShare
                        {
                            RequestId = shareRequestId,
                            QuestId = shareQuestId,
                            ExpectedRevision = shareRevision,
                        },
                    });
                    break;
                case HudCommandKind.AcceptSharedQuest:
                case HudCommandKind.DeclineSharedQuest:
                    if (!TryExpectedRevision(command, out ulong responseRevision)
                        || !TryPositiveUInt64(command, command.Value, "quest-share invite", out ulong inviteId))
                    {
                        return;
                    }

                    if (command.SecondaryValue.IsEmpty)
                    {
                        RejectHudCommand(command, "quest-share response has no quest identifier");
                        return;
                    }

                    if (!TryNextHudRequestId(out ulong responseRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        QuestShareResponse = new QuestShareResponse
                        {
                            RequestId = responseRequestId,
                            InviteId = inviteId,
                            Accept = command.Kind == HudCommandKind.AcceptSharedQuest,
                            ExpectedRevision = responseRevision,
                        },
                    });
                    break;
                case HudCommandKind.AcceptQuest:
                    if (!TryQuest(command, "accept", out string acceptQuestId, out ulong acceptRevision)
                        || !TryEntity(command, "quest accept", out ulong starterEntityId)
                        || !TryNextHudRequestId(out ulong acceptRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        QuestAccept = new QuestAccept
                        {
                            QuestId = acceptQuestId,
                            StarterEntityId = starterEntityId,
                            RequestId = acceptRequestId,
                            ExpectedRevision = acceptRevision,
                        },
                    });
                    break;
                case HudCommandKind.TurnInQuest:
                    if (!TryQuest(command, "turn in", out string turnInQuestId, out ulong turnInRevision)
                        || !TryEntity(command, "quest turn-in", out ulong finisherEntityId)
                        || !TrySlot(command, command.Slot, 5, "quest reward", out uint rewardIndex)
                        || !TryNextHudRequestId(out ulong turnInRequestId))
                    {
                        return;
                    }

                    EnqueueCommand(new ClientMessage
                    {
                        QuestTurnIn = new QuestTurnIn
                        {
                            QuestId = turnInQuestId,
                            FinisherEntityId = finisherEntityId,
                            RewardIndex = rewardIndex,
                            RequestId = turnInRequestId,
                            ExpectedRevision = turnInRevision,
                        },
                    });
                    break;
                default:
                    RejectHudCommand(command, "no matching product-protocol request exists");
                    return;
            }
        }
    }

    private bool TryExpectedRevision(in HudCommand command, out ulong revision)
    {
        revision = command.ExpectedRevision.Revision;
        if (_hudSession is not null
            && command.ExpectedRevision.SourceEpoch == _hudSession.SourceEpoch
            && revision != 0)
        {
            return true;
        }

        RejectHudCommand(
            command,
            $"expected revision {command.ExpectedRevision} does not belong to source epoch {_hudSession?.SourceEpoch ?? 0}");
        return false;
    }

    private bool TryQuest(
        in HudCommand command,
        string operation,
        out string questId,
        out ulong expectedRevision)
    {
        questId = command.Value.Value;
        if (command.Value.IsEmpty)
        {
            expectedRevision = 0;
            RejectHudCommand(command, $"quest {operation} has no quest identifier");
            return false;
        }

        return TryExpectedRevision(command, out expectedRevision);
    }

    private bool TryEntity(in HudCommand command, string operation, out ulong entityId)
    {
        entityId = command.EntityId;
        if (entityId != 0)
        {
            return true;
        }

        RejectHudCommand(command, $"{operation} has no entity identifier");
        return false;
    }

    private bool TrySlot(in HudCommand command, int slot, int count, string operation, out uint value)
    {
        if ((uint)slot < (uint)count)
        {
            value = checked((uint)slot);
            return true;
        }

        value = 0;
        RejectHudCommand(command, $"{operation} slot {slot} is outside 0..{count - 1}");
        return false;
    }

    private bool TryPositiveUInt64(in HudCommand command, HudId value, string operation, out ulong parsed)
    {
        if (!value.IsEmpty
            && ulong.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
            && parsed != 0)
        {
            return true;
        }

        parsed = 0;
        RejectHudCommand(command, $"{operation} identifier '{value.Value}' is not a positive integer");
        return false;
    }

    private bool TryNextHudRequestId(out ulong requestId)
    {
        requestId = _nextHudRequestId;
        if (requestId == 0)
        {
            _hudSession?.ReportTransportFault("Native HUD request identifier space is exhausted for this session.");
            return false;
        }

        _nextHudRequestId = unchecked(requestId + 1);
        return true;
    }

    private void RejectHudCommand(in HudCommand command, string reason) =>
        _hudSession?.ReportTransportFault($"HUD command {command.Kind} was rejected: {reason}.");

    private sealed record ConnectionUpdate(
        ulong OwnEntityId,
        Sarnaut.Protocol.V1.Vec3? SpawnPosition,
        string? ServerBuildId,
        string? Error)
    {
        public static ConnectionUpdate Ready(
            ulong ownEntityId,
            Sarnaut.Protocol.V1.Vec3 spawnPosition,
            string serverBuildId) => new(ownEntityId, spawnPosition, serverBuildId, null);

        public static ConnectionUpdate Failed(string error) => new(0, null, null, error);
    }
}
