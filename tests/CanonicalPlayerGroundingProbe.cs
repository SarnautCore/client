using System;
using System.Globalization;
using System.Threading.Tasks;
using Godot;

namespace SarnautCore;

/// <summary>
/// Replays the canonical League tutorial position through the production zone,
/// then measures physical and rendered contact with the authored floor.
/// </summary>
public partial class CanonicalPlayerGroundingProbe : Node
{
    // data/overlays/m2-slice/zone.inst-league1.yaml curates tile-local
    // (110, 170.5, 156.293) as the player start. Tile 1_2 contributes the
    // canonical server origin (256, 5632, 0), then OnlineCoordinateFrame maps
    // server (366, 5802.5, 156.293) to this Godot point.
    private static readonly Vector3 CanonicalPosition = new(366.0f, 156.293f, -5802.5f);

    public override async void _Ready()
    {
        try
        {
            await RunAsync();
        }
        catch (Exception exception)
        {
            GD.PrintErr($"CANONICAL_PLAYER_GROUNDING result=FAIL error={exception}");
            GetTree().Quit(1);
        }
    }

    private async Task RunAsync()
    {
        Node zone = ResourceLoader.Load<PackedScene>("res://scenes/zone_walkabout.tscn").Instantiate();
        AddChild(zone);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var walker = zone.GetNode<WalkaboutController>("Walker");
        walker.InputEnabled = false;
        walker.LookInputEnabled = false;
        // This is the online controller state: snapshot reconciliation owns
        // position and WalkaboutController deliberately does not apply gravity.
        walker.NetworkControlled = true;
        walker.Velocity = Vector3.Zero;
        walker.ApplyAuthoritativePosition(CanonicalPosition);

        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var collision = walker.GetNode<CollisionShape3D>("CollisionShape3D");
        float canonicalCapsuleBottom = CollisionBottom(collision);
        Godot.Collections.Dictionary canonicalHit = CastFloorRay(walker, canonicalCapsuleBottom);
        bool canonicalHasPosition = canonicalHit.TryGetValue("position", out Variant canonicalPositionValue);
        bool canonicalHasCollider = canonicalHit.TryGetValue("collider", out Variant canonicalColliderValue);
        bool canonicalHasFloor = canonicalHasPosition && canonicalHasCollider;
        Vector3 canonicalFloor = canonicalHasFloor
            ? canonicalPositionValue.AsVector3()
            : new(float.NaN, float.NaN, float.NaN);
        var canonicalCollider = canonicalHasFloor ? canonicalColliderValue.AsGodotObject() as Node : null;
        float canonicalPhysicalGap = canonicalHasFloor
            ? canonicalCapsuleBottom - canonicalFloor.Y
            : float.PositiveInfinity;
        Vector3 networkedBodyPosition = walker.GlobalPosition;
        float networkedHorizontalError = new Vector2(
            networkedBodyPosition.X - CanonicalPosition.X,
            networkedBodyPosition.Z - CanonicalPosition.Z).Length();
        float networkedGroundingAdjustment = walker.NetworkGroundingAdjustment;
        SurfaceHit visibleSupport = FindVisibleSupport(zone, canonicalCapsuleBottom);
        int terrainCollisionShapes = CountCollisionShapes(zone.GetNode("ZoneLoader/Terrain"));
        int staticCollisionShapes = CountCollisionShapes(zone.GetNode("ZoneLoader/StaticObjects"));

        // Now remove reconciliation's freeze and let the production offline
        // controller answer whether physics agrees with the server position.
        walker.NetworkControlled = false;

        for (int frame = 0; frame < 120; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }

        float capsuleBottom = CollisionBottom(collision);
        Godot.Collections.Dictionary hit = CastFloorRay(walker, capsuleBottom);
        bool hasPosition = hit.TryGetValue("position", out Variant positionValue);
        bool hasCollider = hit.TryGetValue("collider", out Variant colliderValue);
        bool hasFloor = hasPosition && hasCollider;
        Vector3 floor = hasFloor ? positionValue.AsVector3() : new(float.NaN, float.NaN, float.NaN);
        var collider = hasFloor ? colliderValue.AsGodotObject() as Node : null;
        float physicalGap = hasFloor ? capsuleBottom - floor.Y : float.PositiveInfinity;

        CharacterRig character = walker.GetNode<CharacterRig>("Character");
        float visualBottom = FindVisualBottom(character);
        float visualGap = hasFloor ? visualBottom - floor.Y : float.PositiveInfinity;
        float drop = CanonicalPosition.Y - walker.GlobalPosition.Y;
        string colliderPath = collider?.GetPath().ToString() ?? "<none>";

        string screenshotPath = Setting("SARNAUT_GROUNDING_SCREENSHOT", string.Empty);
        bool screenshotSaved = await CaptureScreenshotAsync(screenshotPath);
        bool screenshotRequired = screenshotPath.Length > 0;

        const float contactTolerance = 0.08f;
        const float canonicalTolerance = 0.25f;
        const float visualTolerance = 0.12f;
        bool contact = hasFloor && Math.Abs(physicalGap) <= contactTolerance;
        bool canonicalContact = canonicalHasFloor && Math.Abs(canonicalPhysicalGap) <= contactTolerance;
        bool authoritativeHorizontalPositionPreserved = networkedHorizontalError <= 0.001f;
        bool stayedAtCanonicalFloor = Math.Abs(drop) <= canonicalTolerance;
        bool visualContact = hasFloor && Math.Abs(visualGap) <= visualTolerance;
        bool passed = canonicalContact
            && authoritativeHorizontalPositionPreserved
            && contact
            && stayedAtCanonicalFloor
            && visualContact
            && (!screenshotRequired || screenshotSaved);

        GD.Print(
            "CANONICAL_PLAYER_GROUNDING "
            + $"canonical={CanonicalPosition} body={walker.GlobalPosition} drop={drop.ToString("F4", CultureInfo.InvariantCulture)} "
            + $"canonical_floor={canonicalFloor} canonical_physical_gap={canonicalPhysicalGap.ToString("F4", CultureInfo.InvariantCulture)} "
            + $"canonical_collider={canonicalCollider?.GetPath().ToString() ?? "<none>"} "
            + $"networked_body={networkedBodyPosition} networked_xz_error={networkedHorizontalError.ToString("F4", CultureInfo.InvariantCulture)} "
            + $"networked_grounding_adjustment={networkedGroundingAdjustment.ToString("F4", CultureInfo.InvariantCulture)} "
            + $"visible_support={visibleSupport.Position} visible_gap={visibleSupport.Gap.ToString("F4", CultureInfo.InvariantCulture)} "
            + $"visible_support_mesh={visibleSupport.Path} terrain_shapes={terrainCollisionShapes} static_shapes={staticCollisionShapes} "
            + $"capsule_bottom={capsuleBottom.ToString("F4", CultureInfo.InvariantCulture)} "
            + $"visual_bottom={visualBottom.ToString("F4", CultureInfo.InvariantCulture)} "
            + $"floor={floor} physical_gap={physicalGap.ToString("F4", CultureInfo.InvariantCulture)} "
            + $"visual_gap={visualGap.ToString("F4", CultureInfo.InvariantCulture)} collider={colliderPath} "
            + $"canonical_contact={canonicalContact} authoritative_xz={authoritativeHorizontalPositionPreserved} "
            + $"contact={contact} stayed_at_canonical={stayedAtCanonicalFloor} "
            + $"visual_contact={visualContact} "
            + $"screenshot={(screenshotRequired ? screenshotPath : "<not-requested>")} result={(passed ? "PASS" : "FAIL")}");

        if (!passed)
        {
            GD.PushError("CANONICAL_PLAYER_GROUNDING feet do not remain in measurable contact at the canonical tutorial position");
        }

        zone.QueueFree();
        GetTree().Quit(passed ? 0 : 1);
    }

    private Godot.Collections.Dictionary CastFloorRay(WalkaboutController walker, float capsuleBottom)
    {
        Vector3 start = new(walker.GlobalPosition.X, capsuleBottom + 0.15f, walker.GlobalPosition.Z);
        Vector3 end = new(walker.GlobalPosition.X, capsuleBottom - 100.0f, walker.GlobalPosition.Z);
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(start, end, collisionMask: 1);
        query.Exclude = [walker.GetRid()];
        return GetViewport().World3D.DirectSpaceState.IntersectRay(query);
    }

    private static float CollisionBottom(CollisionShape3D collision)
    {
        if (collision.Shape is not CapsuleShape3D capsule)
        {
            throw new InvalidOperationException("The production walker no longer has its capsule collision shape.");
        }

        return collision.GlobalPosition.Y - (capsule.Height * 0.5f * collision.GlobalBasis.Scale.Y);
    }

    private static float FindVisualBottom(Node root)
    {
        float minimum = float.PositiveInfinity;
        Visit(root, ref minimum);
        if (float.IsPositiveInfinity(minimum))
        {
            throw new InvalidOperationException("The production player has no rendered MeshInstance3D bounds.");
        }

        return minimum;
    }

    private static int CountCollisionShapes(Node root)
    {
        int count = root is CollisionShape3D ? 1 : 0;
        foreach (Node child in root.GetChildren())
        {
            count += CountCollisionShapes(child);
        }

        return count;
    }

    private static SurfaceHit FindVisibleSupport(Node root, float feetY)
    {
        var best = new SurfaceHit(new Vector3(float.NaN, float.NaN, float.NaN), float.PositiveInfinity, "<none>");
        VisitSupport(root, feetY, ref best);
        return best;
    }

    private static void VisitSupport(Node node, float feetY, ref SurfaceHit best)
    {
        if (node is MeshInstance3D meshInstance && meshInstance.Mesh != null && meshInstance.IsVisibleInTree())
        {
            if (meshInstance.Mesh is not ArrayMesh mesh)
            {
                goto VisitChildren;
            }

            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                if (mesh.SurfaceGetPrimitiveType(surface) != Mesh.PrimitiveType.Triangles)
                {
                    continue;
                }

                Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
                Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                int triangleValues = indices.Length > 0 ? indices.Length : vertices.Length;
                for (int value = 0; value + 2 < triangleValues; value += 3)
                {
                    int ai = indices.Length > 0 ? indices[value] : value;
                    int bi = indices.Length > 0 ? indices[value + 1] : value + 1;
                    int ci = indices.Length > 0 ? indices[value + 2] : value + 2;
                    Vector3 a = meshInstance.GlobalTransform * vertices[ai];
                    Vector3 b = meshInstance.GlobalTransform * vertices[bi];
                    Vector3 c = meshInstance.GlobalTransform * vertices[ci];
                    if (!TryVerticalIntersection(CanonicalPosition.X, CanonicalPosition.Z, a, b, c, out float y))
                    {
                        continue;
                    }

                    float gap = feetY - y;
                    if (gap >= -0.02f && gap <= 100.0f && gap < best.Gap)
                    {
                        best = new SurfaceHit(
                            new Vector3(CanonicalPosition.X, y, CanonicalPosition.Z),
                            gap,
                            meshInstance.GetPath().ToString());
                    }
                }
            }
        }

    VisitChildren:
        foreach (Node child in node.GetChildren())
        {
            VisitSupport(child, feetY, ref best);
        }
    }

    private static bool TryVerticalIntersection(
        float x,
        float z,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float y)
    {
        float denominator = ((b.Z - c.Z) * (a.X - c.X)) + ((c.X - b.X) * (a.Z - c.Z));
        if (Math.Abs(denominator) <= 0.000001f)
        {
            y = 0;
            return false;
        }

        float wa = (((b.Z - c.Z) * (x - c.X)) + ((c.X - b.X) * (z - c.Z))) / denominator;
        float wb = (((c.Z - a.Z) * (x - c.X)) + ((a.X - c.X) * (z - c.Z))) / denominator;
        float wc = 1.0f - wa - wb;
        const float tolerance = -0.0001f;
        if (wa < tolerance || wb < tolerance || wc < tolerance)
        {
            y = 0;
            return false;
        }

        y = (wa * a.Y) + (wb * b.Y) + (wc * c.Y);
        return true;
    }

    private static void Visit(Node node, ref float minimum)
    {
        if (node is MeshInstance3D meshInstance && meshInstance.Mesh != null && meshInstance.Visible)
        {
            Aabb bounds = meshInstance.GetAabb();
            for (int index = 0; index < 8; index++)
            {
                Vector3 corner = bounds.GetEndpoint(index);
                minimum = Math.Min(minimum, (meshInstance.GlobalTransform * corner).Y);
            }
        }

        foreach (Node child in node.GetChildren())
        {
            Visit(child, ref minimum);
        }
    }

    private async Task<bool> CaptureScreenshotAsync(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        return GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok;
    }

    private static string Setting(string variable, string fallback)
    {
        string? value = System.Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private readonly record struct SurfaceHit(Vector3 Position, float Gap, string Path);
}
