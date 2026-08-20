using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Godot;
using Sarnaut.Protocol.V1;
using SarnautCore.Networking;
using SarnautCore.Shell;

namespace SarnautCore;

public partial class ZoneNetworkLoop : Node
{
    private const double SendIntervalSeconds = 1.0 / 20.0;
    private const double StatusIntervalSeconds = 0.25;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<SnapshotBatch> _receivedSnapshots = new();
    private readonly ConcurrentQueue<ConnectionUpdate> _connectionUpdates = new();
    private readonly Channel<ClientMoveIntent> _moveIntents = Channel.CreateBounded<ClientMoveIntent>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly SnapshotTimeline _timeline = new();
    private readonly MoveIntentCadence _cadence = new(SendIntervalSeconds);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private WalkaboutController _walker = null!;
    private ZoneEntityVisualFactory _visuals = null!;
    private ZoneEntityPicker _picker = null!;
    private Action<string> _setStatus = null!;
    private Action? _onAdmitted;
    private Action<string>? _onRefused;
    private Task? _networkTask;
    private ulong _ownEntityId;
    private double _statusAccumulator;
    private bool _connected;

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

    /// <summary>The entity the player has selected, or 0 for none.</summary>
    public ulong TargetEntityId { get; private set; }

    /// <summary>Raised when the selection changes, with 0 for a cleared target.</summary>
    public event Action<ulong>? TargetChanged;

    /// <param name="ticket">
    /// The opaque single-use shard ticket the account service minted for this
    /// character (ADR 0030). It travels in <c>EnterZoneRequest</c> and the shard
    /// burns it on redemption; nothing here keeps or prints a copy.
    /// </param>
    public void Start(
        WalkaboutController walker,
        Node3D entityRoot,
        EntityModelCatalog catalog,
        string convertedRoot,
        string address,
        string zoneId,
        Secret ticket,
        Action<string> setStatus,
        Action? onAdmitted = null,
        Action<string>? onRefused = null)
    {
        _walker = walker;
        _visuals = new ZoneEntityVisualFactory(entityRoot, catalog, convertedRoot);
        Entities = new EntityRegistry(_visuals);
        _picker = new ZoneEntityPicker();
        AddChild(_picker);
        _setStatus = setStatus;
        _onAdmitted = onAdmitted;
        _onRefused = onRefused;
        _walker.NetworkControlled = true;
        _setStatus($"Online: connecting to {address}...");
        _networkTask = RunNetworkAsync(address, zoneId, ticket, _shutdown.Token);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_connected)
        {
            return;
        }

        _cadence.Accumulate(delta);
        Vector3 direction = _walker.NetworkMoveDirection;
        while (_cadence.TryDequeue(direction.X, direction.Z, _walker.Rotation.Y, out ClientMoveIntent intent))
        {
            _moveIntents.Writer.TryWrite(intent);
        }
    }

    public override void _Process(double delta)
    {
        DrainConnectionUpdates();
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
        _shutdown.Dispose();
    }

    /// <summary>
    /// Selects the entity under a point on screen, and returns false when the
    /// ray hit nothing the registry knows.
    /// </summary>
    public bool TryTargetAtScreenPoint(Vector2 screenPoint, out ulong entityId)
    {
        entityId = 0;
        if (Entities is null)
        {
            return false;
        }

        if (!_picker.TryPick(GetViewport().GetCamera3D(), screenPoint, out ulong pickKey)
            || !Entities.TryGetByPickKey(pickKey, out TrackedEntity? hit))
        {
            // Clicking past everything is how a target is dropped.
            SetTarget(0);
            return false;
        }

        entityId = hit.EntityId;
        SetTarget(entityId);
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
        if (TargetEntityId == entityId)
        {
            return;
        }

        if (TargetEntityId != 0 && Entities.TryGet(TargetEntityId, out TrackedEntity? previous)
            && previous.Visual is NetworkEntityVisual previousVisual)
        {
            previousVisual.Targeted = false;
        }

        TargetEntityId = entityId;
        if (entityId != 0 && Entities.TryGet(entityId, out TrackedEntity? current)
            && current.Visual is NetworkEntityVisual currentVisual)
        {
            currentVisual.Targeted = true;
        }

        TargetChanged?.Invoke(entityId);
    }

    private async Task RunNetworkAsync(
        string address,
        string zoneId,
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
                ticket: ticket.Reveal(),
                cancellationToken: cancellationToken);

            _connectionUpdates.Enqueue(ConnectionUpdate.Ready(
                session.EnteredZone.OwnEntityId,
                session.EnteredZone.SpawnPosition ?? new Sarnaut.Protocol.V1.Vec3(),
                session.ServerHello.BuildId));
            try
            {
                await Task.WhenAll(
                    ReceiveSnapshotsAsync(session, cancellationToken),
                    SendMovementAsync(session, cancellationToken));
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

    private async Task ReceiveSnapshotsAsync(GameSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SnapshotBatch snapshot = await session.ReadSnapshotAsync(cancellationToken);
            _receivedSnapshots.Enqueue(snapshot);
        }
    }

    private async Task SendMovementAsync(GameSession session, CancellationToken cancellationToken)
    {
        await foreach (ClientMoveIntent intent in _moveIntents.Reader.ReadAllAsync(cancellationToken))
        {
            await session.SendMoveIntentAsync(intent, cancellationToken);
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
            // The response's spawn is authoritative: it is the server's answer,
            // not a confirmation of a client request (session spec rule 5.4.6).
            _walker.Position = ToGodot(update.SpawnPosition!);
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
            _walker.Position = new Vector3(local.X, local.Z, local.Y);
        }

        if (TargetEntityId != 0 && !Entities.Contains(TargetEntityId))
        {
            // The shard stopped replicating the target: it died and despawned,
            // or it left the subscription. Either way the selection is gone.
            SetTarget(0);
        }
    }

    private static Vector3 ToGodot(Sarnaut.Protocol.V1.Vec3 value) => new(value.X, value.Z, value.Y);

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
