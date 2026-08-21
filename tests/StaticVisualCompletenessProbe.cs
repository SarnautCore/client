using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace SarnautCore;

public partial class StaticVisualCompletenessProbe : Node
{
    private const int ExpectedPlacements = 41;
    private const int ExpectedVisualPlacements = 36;
    private const int ExpectedNonVisualPlacements = 5;
    private const int ExpectedNonVisualCollisionPlacements = 1;
    private const int ExpectedSceneResources = 24;
    private const int ExpectedReceiverMeshes = 4;
    private const int ExpectedTexturelessMarkers = 16;

    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        Node3D objects = loader.GetNode<Node3D>("StaticObjects");
        Node3D[] placements = objects.GetChildren().OfType<Node3D>().ToArray();
        Node3D[] rendered = placements.Where(placement => MeshesBelow(placement).Length > 0).ToArray();
        NativePlacement[] baked = ReadBakedPlacements(loader).ToArray();

        int orderOrTransformMismatches = CountManifestMismatches(baked, placements);
        int invalidTransforms = placements.Count(HasInvalidTransform);
        int missingNativeMetadata = placements.Count(placement =>
            !placement.GetMeta("native_static", false).AsBool()
            || !placement.HasMeta("native_visual")
            || !placement.HasMeta("native_collision")
            || !placement.HasMeta("native_classification"));
        int retiredMetadata = placements.Count(placement =>
            placement.HasMeta("allods_template") || placement.HasMeta("allods_resolution"));
        int emptyMeshes = rendered.Count(placement => MeshesBelow(placement).Any(mesh => mesh.Mesh == null));
        int unmaterialedMeshes = rendered.Count(placement => MeshesBelow(placement).Any(MeshHasNoMaterial));
        int nativeSceneInstances = rendered.Count(placement =>
            placement.GetMeta("native_scene", string.Empty).AsString().StartsWith(
                NativeContentSettings.NativeRoot,
                StringComparison.Ordinal));
        int nativeSceneResources = rendered
            .Select(placement => placement.GetMeta("native_scene", string.Empty).AsString())
            .Distinct(StringComparer.Ordinal)
            .Count();
        MeshInstance3D[] meshes = rendered.SelectMany(MeshesBelow).ToArray();
        int bakedSurfaces = meshes.Sum(mesh => CountSurfaces(mesh, unshaded: true));
        int runtimeLitSurfaces = meshes.Sum(mesh => CountSurfaces(mesh, unshaded: false));
        int lightingLayerMismatches = meshes.Count(mesh =>
        {
            bool hasRuntimeLitSurface = CountSurfaces(mesh, unshaded: false) > 0;
            uint expectedLayers = hasRuntimeLitSurface
                ? DynamicEntityLighting.ReceiverLayers
                : DynamicEntityLighting.BakedOnlyLayers;
            return mesh.Layers != expectedLayers;
        });
        int receiverMeshes = meshes.Count(mesh =>
            (mesh.Layers & DynamicEntityLighting.ReceiverLayerMask) != 0);
        int collisionMismatches = placements.Count(placement =>
        {
            bool expectsCollision = placement.GetMeta("native_collision", false).AsBool();
            bool hasCollision = placement is StaticBody3D
                || placement.FindChildren("*", "StaticBody3D", true, false).Count > 0;
            return expectsCollision != hasCollision;
        });
        int nonVisualCollisionScenes = placements.Count(placement =>
            !placement.GetMeta("native_visual", false).AsBool()
            && placement.GetMeta("native_collision", false).AsBool()
            && placement.GetMeta("native_scene", string.Empty).AsString().StartsWith(
                NativeContentSettings.NativeRoot,
                StringComparison.Ordinal));
        Node3D[] textureless = rendered.Where(placement =>
            MeshesBelow(placement).Sum(mesh => mesh.Mesh?.GetSurfaceCount() ?? 0) > 0
            && MeshesBelow(placement).Sum(CountTexturedSurfaces) == 0).ToArray();

        bool passed = baked.Length == ExpectedPlacements
            && placements.Length == ExpectedPlacements
            && loader.PlacedObjectCount == ExpectedPlacements
            && loader.NativeStaticPlacementCount == ExpectedPlacements
            && loader.VisualObjectCount == ExpectedVisualPlacements
            && loader.NativeStaticVisualCount == ExpectedVisualPlacements
            && loader.NonVisualObjectCount == ExpectedNonVisualPlacements
            && loader.NativeStaticNonVisualCount == ExpectedNonVisualPlacements
            && nonVisualCollisionScenes == ExpectedNonVisualCollisionPlacements
            && rendered.Length == ExpectedVisualPlacements
            && nativeSceneInstances == ExpectedVisualPlacements
            && nativeSceneResources == ExpectedSceneResources
            && bakedSurfaces > 0
            && runtimeLitSurfaces > 0
            && receiverMeshes == ExpectedReceiverMeshes
            && receiverMeshes == loader.NativeStaticReceiverMeshCount
            && textureless.Length == ExpectedTexturelessMarkers
            && orderOrTransformMismatches == 0
            && invalidTransforms == 0
            && missingNativeMetadata == 0
            && retiredMetadata == 0
            && lightingLayerMismatches == 0
            && collisionMismatches == 0
            && emptyMeshes == 0
            && unmaterialedMeshes == 0;

        GD.Print(
            $"STATIC_VISUAL_PROBE baked={baked.Length} placements={loader.PlacedObjectCount} "
            + $"native={loader.NativeStaticPlacementCount} visual={loader.VisualObjectCount} "
            + $"non_visual={loader.NonVisualObjectCount} "
            + $"rendered={rendered.Length} "
            + $"native_scenes={nativeSceneInstances}/{nativeSceneResources} "
            + $"baked_surfaces={bakedSurfaces} runtime_lit_surfaces={runtimeLitSurfaces} "
            + $"receiver_meshes={receiverMeshes} lighting_layer_mismatches={lightingLayerMismatches} "
            + $"collision_mismatches={collisionMismatches} nonvisual_collision_scenes={nonVisualCollisionScenes} "
            + $"textureless_markers={textureless.Length} "
            + $"order_transform_mismatches={orderOrTransformMismatches} invalid_transforms={invalidTransforms} "
            + $"missing_native_metadata={missingNativeMetadata} retired_metadata={retiredMetadata} "
            + $"empty_meshes={emptyMeshes} unmaterialed={unmaterialedMeshes} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        if (!passed && !string.IsNullOrWhiteSpace(loader.LastError))
        {
            GD.PushError(loader.LastError);
        }

        GetTree().Quit(passed ? 0 : 1);
    }

    private static IReadOnlyList<NativePlacement> ReadBakedPlacements(ZoneLoader loader)
    {
        string root = $"{NativeContentSettings.NativeRoot}/maps/"
            + $"{MapNameTransform.ToKebabCase(loader.MapName)}/statics";
        var result = new List<NativePlacement>();
        using JsonDocument bake = JsonDocument.Parse(FileAccess.GetFileAsString($"{root}/bake.json"));
        foreach (JsonElement cell in bake.RootElement.GetProperty("cells").EnumerateArray())
        {
            string path = $"{root}/{cell.GetProperty("placements").GetString()}";
            using JsonDocument document = JsonDocument.Parse(FileAccess.GetFileAsString(path));
            foreach (JsonElement placement in document.RootElement.GetProperty("placements").EnumerateArray())
            {
                float[] position = placement.GetProperty("position").EnumerateArray()
                    .Select(value => value.GetSingle()).ToArray();
                float[] rotation = placement.GetProperty("rotation").EnumerateArray()
                    .Select(value => value.GetSingle()).ToArray();
                float scale = placement.GetProperty("scale").GetSingle();
                result.Add(new NativePlacement(
                    placement.GetProperty("name").GetString() ?? string.Empty,
                    new Vector3(position[0], position[1], position[2]),
                    new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]),
                    Vector3.One * scale,
                    placement.GetProperty("collision").GetBoolean(),
                    placement.GetProperty("visual").GetBoolean(),
                    placement.GetProperty("classification").GetString() ?? string.Empty));
            }
        }

        return result;
    }

    private static int CountManifestMismatches(NativePlacement[] baked, Node3D[] instances)
    {
        if (baked.Length != instances.Length)
        {
            return Math.Max(baked.Length, instances.Length);
        }

        int mismatches = 0;
        for (int index = 0; index < baked.Length; index++)
        {
            NativePlacement expected = baked[index];
            Node3D actual = instances[index];
            bool nameMatches = actual.Name.ToString().Equals(expected.Name, StringComparison.Ordinal);
            bool positionMatches = actual.Position.IsEqualApprox(expected.Position);
            bool scaleMatches = actual.Scale.IsEqualApprox(expected.Scale);
            bool rotationMatches = MathF.Abs(actual.Quaternion.Dot(expected.Rotation)) >= 0.99999f;
            bool classificationMatches = actual.GetMeta("native_visual", false).AsBool() == expected.Visual;
            bool classificationNameMatches = actual.GetMeta("native_classification", string.Empty).AsString()
                .Equals(expected.Classification, StringComparison.Ordinal);
            bool collisionMatches = actual.GetMeta("native_collision", false).AsBool() == expected.Collision;
            if (!nameMatches
                || !positionMatches
                || !scaleMatches
                || !rotationMatches
                || !classificationMatches
                || !classificationNameMatches
                || !collisionMatches)
            {
                mismatches++;
            }
        }

        return mismatches;
    }

    private static bool HasInvalidTransform(Node3D placement)
    {
        Vector3 position = placement.Position;
        Vector3 scale = placement.Scale;
        return !float.IsFinite(position.X)
            || !float.IsFinite(position.Y)
            || !float.IsFinite(position.Z)
            || !float.IsFinite(scale.X)
            || !float.IsFinite(scale.Y)
            || !float.IsFinite(scale.Z)
            || scale.X <= 0
            || scale.Y <= 0
            || scale.Z <= 0;
    }

    private static MeshInstance3D[] MeshesBelow(Node3D placement)
    {
        MeshInstance3D[] descendants = placement.FindChildren("*", "MeshInstance3D", true, false)
            .OfType<MeshInstance3D>()
            .ToArray();
        return placement is MeshInstance3D rootMesh
            ? descendants.Prepend(rootMesh).ToArray()
            : descendants;
    }

    private static bool MeshHasNoMaterial(MeshInstance3D mesh)
    {
        if (mesh.MaterialOverride != null || mesh.Mesh == null)
        {
            return false;
        }

        return Enumerable.Range(0, mesh.Mesh.GetSurfaceCount())
            .Any(surface => mesh.GetSurfaceOverrideMaterial(surface) == null
                && mesh.Mesh.SurfaceGetMaterial(surface) == null);
    }

    private static int CountSurfaces(MeshInstance3D mesh, bool unshaded)
    {
        if (mesh.Mesh == null)
        {
            return 0;
        }

        return Enumerable.Range(0, mesh.Mesh.GetSurfaceCount())
            .Count(surface => mesh.GetActiveMaterial(surface) is BaseMaterial3D material
                && (material.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded) == unshaded);
    }

    private static int CountTexturedSurfaces(MeshInstance3D mesh)
    {
        if (mesh.Mesh == null)
        {
            return 0;
        }

        return Enumerable.Range(0, mesh.Mesh.GetSurfaceCount())
            .Count(surface => mesh.GetActiveMaterial(surface) is BaseMaterial3D { AlbedoTexture: not null });
    }

    private sealed record NativePlacement(
        string Name,
        Vector3 Position,
        Quaternion Rotation,
        Vector3 Scale,
        bool Collision,
        bool Visual,
        string Classification);
}
