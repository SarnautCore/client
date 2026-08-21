using Godot;
using Sarnaut.Protocol.V1;
using SarnautCore.Networking;

namespace SarnautCore;

/// <summary>
/// Locks the server-to-Godot boundary to an asymmetric authored map point.
/// The expected global value is independent of the loader work that adds
/// sector and tile origins to tile-local converted placements.
/// </summary>
public partial class OnlineCoordinateFrameSmoke : Node3D
{
    private static readonly Vector3 AuthoredGodotPoint = new(317.1128f, 56.4219f, -5873.054f);

    public override void _Ready()
    {
        var wirePoint = new Vec3 { X = 317.1128f, Y = 5873.054f, Z = 56.4219f };
        Vector3 spawnPoint = OnlineCoordinateFrame.ToGodot(wirePoint);

        var entityRoot = new Node3D { Name = "NetworkEntities" };
        AddChild(entityRoot);
        var factory = new ZoneEntityVisualFactory(
            entityRoot,
            new EntityModelCatalog("res://__missing_coordinate_smoke_assets__"));
        var sample = new SampledEntity(
            EntityId: 2,
            Kind: EntityKind.Npc,
            X: wirePoint.X,
            Y: wirePoint.Y,
            Z: wirePoint.Z,
            Heading: 0,
            VelocityX: 0,
            VelocityY: 0,
            VelocityZ: 0,
            AnimationState: AnimationState.Idle,
            ContentId: "smoke.coordinate-frame",
            NameKey: "CoordinateFrameSmoke",
            Level: 1,
            Faction: "smoke",
            Health: 1,
            MaxHealth: 1,
            Alive: true);
        var visual = (NetworkEntityVisual)factory.Create(sample);
        Vector2 serverDirection = OnlineCoordinateFrame.ToServerGround(new Vector3(0.3125f, 0, -0.875f));

        bool spawnPassed = spawnPoint.IsEqualApprox(AuthoredGodotPoint);
        bool remotePassed = visual.Position.IsEqualApprox(AuthoredGodotPoint);
        bool movementPassed = serverDirection.IsEqualApprox(new Vector2(0.3125f, 0.875f));
        bool passed = spawnPassed && remotePassed && movementPassed;
        GD.Print(
            $"ONLINE_COORDINATE_FRAME_SMOKE spawn={spawnPoint} remote={visual.Position} "
            + $"movement={serverDirection} expected={AuthoredGodotPoint} result={(passed ? "PASS" : "FAIL")}");
        if (!spawnPassed)
        {
            GD.PushError($"ONLINE_COORDINATE_FRAME_SMOKE spawn conversion produced {spawnPoint}");
        }

        if (!remotePassed)
        {
            GD.PushError($"ONLINE_COORDINATE_FRAME_SMOKE remote visual stood at {visual.Position}");
        }

        if (!movementPassed)
        {
            GD.PushError($"ONLINE_COORDINATE_FRAME_SMOKE movement conversion produced {serverDirection}");
        }

        GetTree().Quit(passed ? 0 : 1);
    }
}
