using Godot;

namespace SarnautCore;

/// <summary>
/// Turns a point on screen into the entity body under it.
/// </summary>
/// <remarks>
/// One ray is kept and re-aimed rather than a ray per click, because a
/// <see cref="RayCast3D"/> has to be in the tree to query the physics world at
/// all. It collides with areas only, and only on the entity layer: terrain has a
/// <c>StaticBody3D</c> and picking through it is the point, not a bug.
/// </remarks>
public sealed partial class ZoneEntityPicker : RayCast3D
{
    public ZoneEntityPicker()
    {
        Name = "EntityPicker";
        Enabled = false;
        CollideWithAreas = true;
        CollideWithBodies = false;
        CollisionMask = NetworkEntityVisual.EntityCollisionLayer;
        TopLevel = true;
    }

    [Export(PropertyHint.Range, "5,500,5")]
    public float MaxDistanceMetres { get; set; } = 120.0f;

    /// <summary>
    /// Casts through <paramref name="screenPoint"/> and reports the instance id
    /// of the body it hit, which is the registry's pick key.
    /// </summary>
    public bool TryPick(Camera3D? camera, Vector2 screenPoint, out ulong pickKey)
    {
        pickKey = 0;
        if (camera == null || !IsInsideTree())
        {
            return false;
        }

        Vector3 origin = camera.ProjectRayOrigin(screenPoint);
        Vector3 direction = camera.ProjectRayNormal(screenPoint);
        GlobalTransform = new Transform3D(Basis.Identity, origin);
        TargetPosition = direction * MaxDistanceMetres;
        ForceRaycastUpdate();
        if (!IsColliding())
        {
            return false;
        }

        GodotObject? collider = GetCollider();
        if (collider == null)
        {
            return false;
        }

        pickKey = collider.GetInstanceId();
        return true;
    }
}
