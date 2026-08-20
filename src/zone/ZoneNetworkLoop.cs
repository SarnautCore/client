using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Godot;
using Sarnaut.Protocol.V1;
using SarnautCore.Networking;

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
    private readonly Dictionary<ulong, Node3D> _remoteEntities = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private WalkaboutController _walker = null!;
    private Node3D _entityRoot = null!;
    private Action<string> _setStatus = null!;
    private Task? _networkTask;
    private ulong _ownEntityId;
    private ulong _sequence;
    private double _sendAccumulator;
    private double _statusAccumulator;
    private bool _connected;

    [Export(PropertyHint.Range, "0.1,0.15,0.005")]
    public double InterpolationDelaySeconds { get; set; } = 0.125;

    public void Start(
        WalkaboutController walker,
        Node3D entityRoot,
        string address,
        string zoneId,
        Action<string> setStatus)
    {
        _walker = walker;
        _entityRoot = entityRoot;
        _setStatus = setStatus;
        _walker.NetworkControlled = true;
        _setStatus($"Online: connecting to {address}...");
        _networkTask = RunNetworkAsync(address, zoneId, _shutdown.Token);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_connected)
        {
            return;
        }

        _sendAccumulator += delta;
        while (_sendAccumulator >= SendIntervalSeconds)
        {
            _sendAccumulator -= SendIntervalSeconds;
            Vector3 direction = _walker.NetworkMoveDirection;
            _moveIntents.Writer.TryWrite(new ClientMoveIntent
            {
                Seq = ++_sequence,
                Input = new Sarnaut.Protocol.V1.Vec3
                {
                    X = direction.X,
                    Y = direction.Z,
                },
                Heading = _walker.Rotation.Y,
                DtSeconds = (float)(SendIntervalSeconds * 2),
            });
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
                $"position {_walker.Position.X:F1}, {_walker.Position.Z:F1} | {_timeline.LatestEntityIds.Count} entities");
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

    private async Task RunNetworkAsync(string address, string zoneId, CancellationToken cancellationToken)
    {
        try
        {
            GameEndpoint endpoint = GameEndpoint.Parse(address);
            await using GameSession session = await GameSession.ConnectAsync(
                endpoint,
                zoneId,
                "godot-sar20",
                allowUntrustedDevelopmentCertificate: true,
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
                continue;
            }

            _ownEntityId = update.OwnEntityId;
            _walker.Position = ToGodot(update.SpawnPosition!);
            _connected = true;
            _setStatus($"Online: joined as entity {_ownEntityId} | server {update.ServerBuildId} | QUIC stream");
        }
    }

    private void UpdateEntities()
    {
        double now = _clock.Elapsed.TotalSeconds;
        IReadOnlyCollection<ulong> latestIds = _timeline.LatestEntityIds;
        foreach (ulong entityId in latestIds)
        {
            if (!_timeline.TrySample(entityId, now, InterpolationDelaySeconds, out SampledEntity sample))
            {
                continue;
            }

            Vector3 position = new(sample.X, sample.Z, sample.Y);
            if (entityId == _ownEntityId)
            {
                _walker.Position = position;
                continue;
            }

            if (!_remoteEntities.TryGetValue(entityId, out Node3D? entityNode))
            {
                entityNode = CreatePlaceholder(sample);
                _remoteEntities.Add(entityId, entityNode);
                _entityRoot.AddChild(entityNode);
            }

            entityNode.Position = position;
            entityNode.Rotation = new Vector3(0, sample.Heading, 0);
        }

        foreach (ulong staleId in _remoteEntities.Keys.Where(id => !latestIds.Contains(id)).ToArray())
        {
            _remoteEntities[staleId].QueueFree();
            _remoteEntities.Remove(staleId);
        }
    }

    private static Node3D CreatePlaceholder(SampledEntity sample)
    {
        Color color = sample.Kind == EntityKind.Npc ? new Color("e4a853") : new Color("55c8e8");
        var material = new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.75f,
        };
        var mesh = new MeshInstance3D
        {
            Name = "Capsule",
            Mesh = new CapsuleMesh
            {
                Radius = 0.42f,
                Height = 1.8f,
                Material = material,
            },
            Position = new Vector3(0, 0.9f, 0),
        };
        var root = new Node3D { Name = $"Entity_{sample.EntityId}" };
        root.AddChild(mesh);
        return root;
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
