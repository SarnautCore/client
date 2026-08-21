using System;
using System.Linq;
using Godot;

namespace SarnautCore;

/// <summary>
/// Replays the regenerated League terrain extents through the real walkabout
/// scene's spawn property and camera hierarchy.
/// </summary>
public partial class ZoneCameraSpawnSmoke : Node
{
    public override async void _Ready()
    {
        using PackedScene productionResource = ResourceLoader.Load<PackedScene>(
            "res://scenes/zone_walkabout.tscn")
            ?? throw new InvalidOperationException("The production walkabout scene is not loadable.");
        var productionScene = productionResource.Instantiate<Node3D>();
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
        scene.GetNode<CharacterRig>("Walker/Character").AutoLoad = false;
        var loader = scene.GetNode<ZoneLoader>("ZoneLoader");
        loader.AutoLoad = false;
        loader.CreateTerrainCollision = true;
        loader.SpawnNpcVisuals = false;
        AddChild(scene);
        bool loaded = loader.LoadZone(ZoneLoader.DefaultMapName);

        Node3D terrainRoot = loader.GetNode<Node3D>("Terrain");
        Node3D[] terrainTiles = terrainRoot.GetChildren().OfType<Node3D>().ToArray();
        bool compiledNativeTerrain = terrainTiles.Length == 4
            && loader.NativeTerrainTileCount == 4
            && terrainTiles.All(tile =>
            {
                string nativeScene = tile.GetMeta("native_scene", string.Empty).AsString();
                return nativeScene.StartsWith(
                        $"{NativeContentSettings.NativeRoot}/maps/inst-league-start/",
                        StringComparison.Ordinal)
                    && nativeScene.EndsWith(".scn", StringComparison.OrdinalIgnoreCase)
                    && !nativeScene.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                    && !nativeScene.Contains("res://converted", StringComparison.OrdinalIgnoreCase);
            });

        Vector3 spawn = loader.SuggestedSpawnPosition;
        var walker = scene.GetNode<WalkaboutController>("Walker");
        walker.Position = spawn;
        var camera = scene.GetNode<Camera3D>("Walker/Head/SpringArm3D/Camera3D");
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
        bool grounded = loader.HasTerrainBounds
            && ContainsHorizontal(loader.TerrainBounds, spawn)
            && hit.Count > 0
            && Math.Abs(spawn.Y - visibleGround.Y) <= 1.0f;
        bool behind = camera.IsPositionBehind(visibleGround);
        bool inFrustum = camera.IsPositionInFrustum(visibleGround);
        bool framed = !behind && inFrustum;
        bool passed = loaded && compiledNativeTerrain && grounded && framed;
        GD.Print(
            $"ZONE_CAMERA_SPAWN_SMOKE bounds={loader.TerrainBounds} spawn={spawn} "
            + $"ground={visibleGround} camera={camera.GlobalPosition} behind={behind} in_frustum={inFrustum} "
            + $"compiled_native={compiledNativeTerrain} grounded={grounded} framed={framed} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        if (!grounded)
        {
            GD.PushError($"ZONE_CAMERA_SPAWN_SMOKE no terrain exists below {spawn}");
        }

        if (!framed)
        {
            GD.PushError($"ZONE_CAMERA_SPAWN_SMOKE actual walkabout camera does not frame terrain from {spawn}");
        }

        scene.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetTree().Quit(passed ? 0 : 1);
    }

    private static bool ContainsHorizontal(Aabb bounds, Vector3 point) =>
        point.X >= bounds.Position.X
        && point.X <= bounds.End.X
        && point.Z >= bounds.Position.Z
        && point.Z <= bounds.End.Z;

}
