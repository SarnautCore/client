using System;
using Godot;
using SarnautCore.NativeHud;

namespace SarnautCore;

/// <summary>Small scene seam used by native HUD projection and pointer picking.</summary>
public interface INativeHudWorldScene
{
    Camera3D? Camera { get; }
    void MountTargetSelection(Node3D targetSelection);
    bool TryGetAnchor(ulong entityId, out Vector3 anchor);
    bool TryGetGroundFootprint(ulong entityId, out Vector3 position, out float pickRadius);
    bool IsOccluded(Vector3 origin, Vector3 anchor);
    bool TryPickEntity(Vector2 screenPoint, out ulong entityId);
}

internal sealed class GodotHudWorld(INativeHudWorldScene scene) : IHudWorld
{
    public bool TryProject(in HudWorldQuery query, out HudProjection projection)
    {
        projection = default;
        Camera3D? camera = scene.Camera;
        if (camera is null || !scene.TryGetAnchor(query.EntityId, out Vector3 anchor))
        {
            return false;
        }

        Vector3 cameraSpace = camera.GlobalTransform.AffineInverse() * anchor;
        bool behind = camera.IsPositionBehind(anchor);
        Vector2 point = behind ? default : camera.UnprojectPosition(anchor);
        var screen = new HudPoint(point.X, point.Y);
        projection = new HudProjection(
            screen,
            -cameraSpace.Z,
            !behind && query.Viewport.Contains(screen),
            !behind && scene.IsOccluded(camera.GlobalPosition, anchor));
        return true;
    }
}

/// <summary>Zone implementation of the narrow HUD world seam.</summary>
public sealed class ZoneNativeHudWorld(ZoneNetworkLoop loop, Node3D worldRoot) : INativeHudWorldScene
{
    public Camera3D? Camera => worldRoot.GetViewport().GetCamera3D();

    public void MountTargetSelection(Node3D targetSelection)
    {
        ArgumentNullException.ThrowIfNull(targetSelection);
        if (targetSelection.GetParent() is not null)
        {
            throw new InvalidOperationException("Native target-selection decal is already mounted");
        }

        worldRoot.AddChild(targetSelection);
    }

    public bool TryGetAnchor(ulong entityId, out Vector3 anchor)
    {
        anchor = default;
        if (loop.Entities is null
            || !loop.Entities.TryGet(entityId, out SarnautCore.Networking.TrackedEntity? tracked)
            || tracked.Visual is not NetworkEntityVisual visual)
        {
            return false;
        }

        anchor = visual.HudAnchorPosition;
        return true;
    }

    public bool TryGetGroundFootprint(ulong entityId, out Vector3 position, out float pickRadius)
    {
        position = default;
        pickRadius = 0.0f;
        if (loop.Entities is null
            || !loop.Entities.TryGet(entityId, out SarnautCore.Networking.TrackedEntity? tracked)
            || tracked.Visual is not NetworkEntityVisual visual)
        {
            return false;
        }

        position = visual.GlobalPosition;
        pickRadius = visual.PickRadius;
        return pickRadius > 0.0f;
    }

    public bool IsOccluded(Vector3 origin, Vector3 anchor)
    {
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, anchor, 1);
        return worldRoot.GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
    }

    public bool TryPickEntity(Vector2 screenPoint, out ulong entityId) =>
        loop.TryPickEntityAtScreenPoint(screenPoint, out entityId);
}
