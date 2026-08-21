using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Godot;

namespace SarnautCore;

/// <summary>
/// Replays the regenerated League terrain extents through the real walkabout
/// scene's spawn property and camera hierarchy.
/// </summary>
public partial class ZoneCameraSpawnSmoke : Node
{
    private static readonly Aabb[] TileBounds =
    [
        new(new Vector3(200, -23.66f, -5888), new Vector3(56, 80.65f, 128)),
        new(new Vector3(256, -42.08f, -5888), new Vector3(168, 67.96f, 136)),
        new(new Vector3(256, -14.18f, -5952), new Vector3(112, 60.41f, 64)),
        new(new Vector3(1056, -60.65f, -6112), new Vector3(184, 73.27f, 184)),
    ];

    private static readonly Aabb UnionBounds = TileBounds.Aggregate((left, right) => left.Merge(right));

    public override async void _Ready()
    {
        var productionScene = ResourceLoader.Load<PackedScene>("res://scenes/zone_walkabout.tscn").Instantiate<Node3D>();
        // Keep the production children and their transforms, but not the root
        // ZoneWalkabout script, which would start a second map load here.
        var scene = new Node3D { Name = "WalkaboutCameraRig" };
        foreach (Node child in productionScene.GetChildren())
        {
            productionScene.RemoveChild(child);
            child.Owner = null;
            scene.AddChild(child);
        }

        productionScene.Free();
        scene.GetNode<ConvertedCharacter>("Walker/Character").AutoLoad = false;
        AddChild(scene);
        var loader = scene.GetNode<ZoneLoader>("ZoneLoader");
        Set(loader, nameof(ZoneLoader.TerrainBounds), UnionBounds);
        Set(loader, nameof(ZoneLoader.HasTerrainBounds), true);
        Field<List<Aabb>>(loader, "_terrainSpawnBounds").AddRange(TileBounds);
        Field<List<Vector3>>(loader, "_spawnHints").AddRange(
        [
            new Vector3(290, 18.4f, -5812),
            new Vector3(300, 18.1464f, -5800),
            new Vector3(300, 18.2f, -5800),
            new Vector3(311, 17.9f, -5799),
            new Vector3(1100, 5, -6000),
        ]);

        Vector3 spawn = loader.SuggestedSpawnPosition;
        var walker = scene.GetNode<WalkaboutController>("Walker");
        walker.Position = spawn;
        var camera = scene.GetNode<Camera3D>("Walker/Head/SpringArm3D/Camera3D");
        Mesh terrainMesh = ResourceLoader.Load<Mesh>(
            "res://converted/assets/classic-1.1/assets/Maps/Inst_LeagueStart/000_020/1_2.terrain.up.obj");
        var terrain = new MeshInstance3D
        {
            Name = "AuthoredTerrain1_2",
            Mesh = terrainMesh,
            Position = new Vector3(256, 0, -5632),
        };
        var terrainBody = new StaticBody3D { Name = "Collision" };
        terrainBody.AddChild(new CollisionShape3D { Shape = terrainMesh.CreateTrimeshShape() });
        terrain.AddChild(terrainBody);
        scene.AddChild(terrain);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(
            new Vector3(spawn.X, 100, spawn.Z),
            new Vector3(spawn.X, -100, spawn.Z),
            collisionMask: 1);
        query.Exclude = [walker.GetRid()];
        Godot.Collections.Dictionary hit = GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        Vector3 visibleGround = hit.TryGetValue("position", out Variant positionValue)
            ? positionValue.AsVector3()
            : new Vector3(float.NaN, float.NaN, float.NaN);
        bool grounded = TileBounds.Any(tile => ContainsHorizontal(tile, spawn))
            && hit.Count > 0
            && Math.Abs(spawn.Y - visibleGround.Y) <= 1.0f;
        bool behind = camera.IsPositionBehind(visibleGround);
        bool inFrustum = camera.IsPositionInFrustum(visibleGround);
        bool framed = !behind && inFrustum;
        bool passed = grounded && framed;
        GD.Print(
            $"ZONE_CAMERA_SPAWN_SMOKE bounds={UnionBounds} spawn={spawn} "
            + $"ground={visibleGround} camera={camera.GlobalPosition} behind={behind} in_frustum={inFrustum} "
            + $"grounded={grounded} framed={framed} result={(passed ? "PASS" : "FAIL")}");
        if (!grounded)
        {
            GD.PushError($"ZONE_CAMERA_SPAWN_SMOKE no terrain exists below {spawn}");
        }

        if (!framed)
        {
            GD.PushError($"ZONE_CAMERA_SPAWN_SMOKE actual walkabout camera does not frame terrain from {spawn}");
        }

        scene.QueueFree();
        GetTree().Quit(passed ? 0 : 1);
    }

    private static bool ContainsHorizontal(Aabb bounds, Vector3 point) =>
        point.X >= bounds.Position.X
        && point.X <= bounds.End.X
        && point.Z >= bounds.Position.Z
        && point.Z <= bounds.End.Z;

    private static void Set<T>(ZoneLoader loader, string propertyName, T value)
    {
        PropertyInfo property = typeof(ZoneLoader).GetProperty(propertyName)
            ?? throw new InvalidOperationException($"ZoneLoader has no {propertyName} property.");
        property.SetValue(loader, value);
    }

    private static T Field<T>(ZoneLoader loader, string fieldName)
    {
        FieldInfo field = typeof(ZoneLoader).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"ZoneLoader has no {fieldName} field.");
        return (T)(field.GetValue(loader)
            ?? throw new InvalidOperationException($"ZoneLoader field {fieldName} is null."));
    }
}

