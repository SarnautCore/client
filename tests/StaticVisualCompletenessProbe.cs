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
    private const int ExpectedTexturelessMarkers = 16;
    private const string AiMarkerTemplate = "InstLeague1_AI_2X2.(StaticObject).xdb";
    private const string MapRegionSuffix = "_MapRegion.xdb.placements.json";

    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        Node3D objects = loader.GetNode<Node3D>("StaticObjects");
        Node3D[] placements = objects.GetChildren().OfType<Node3D>().ToArray();
        Node3D[] rendered = placements.Where(placement => MeshesBelow(placement).Length > 0).ToArray();

        if (System.Environment.GetEnvironmentVariable("SARNAUT_EXPECT_STATIC_IMPORT_FAILURE") == "1")
        {
            bool expectedFailure = loader.VisualObjectCount == 0
                && loader.NonVisualObjectCount == ExpectedNonVisualPlacements
                && loader.UnresolvedObjectCount == ExpectedVisualPlacements
                && rendered.Length == 0
                && loader.StaticModelFailures.Count > 0
                && loader.LastError.Contains("Import", StringComparison.OrdinalIgnoreCase);
            GD.Print(
                $"STATIC_IMPORT_PRECONDITION visual={loader.VisualObjectCount} "
                + $"non_visual={loader.NonVisualObjectCount} unresolved={loader.UnresolvedObjectCount} "
                + $"failures={loader.StaticModelFailures.Count} error_reported={!string.IsNullOrWhiteSpace(loader.LastError)} "
                + $"result={(expectedFailure ? "PASS" : "FAIL")}");
            GetTree().Quit(expectedFailure ? 0 : 1);
            return;
        }

        AuthoredPlacement[] authored = ReadAuthoredPlacements(loader).ToArray();
        int transformMismatches = CountTransformMismatches(authored, placements);
        int invalidTransforms = placements.Count(HasInvalidTransform);
        int missingTemplates = placements.Count(placement =>
            !placement.HasMeta("allods_template")
            || string.IsNullOrWhiteSpace(placement.GetMeta("allods_template").AsString()));
        int emptyMeshes = rendered.Count(placement => MeshesBelow(placement).Any(mesh => mesh.Mesh == null));
        int unmaterialedMeshes = rendered.Count(placement => MeshesBelow(placement).Any(MeshHasNoMaterial));
        int sceneResolved = rendered.Count(placement =>
            placement.GetMeta("allods_resolution", string.Empty).AsString() == "scene");
        int geometryFallbacks = rendered.Count(placement =>
            placement.GetMeta("allods_resolution", string.Empty).AsString() == "geometry_fallback");
        Node3D[] textureless = rendered.Where(placement =>
            MeshesBelow(placement).Sum(mesh => mesh.Mesh?.GetSurfaceCount() ?? 0) > 0
            && MeshesBelow(placement).Sum(CountTexturedSurfaces) == 0).ToArray();
        int unexpectedTextureless = textureless.Count(placement =>
            !placement.GetMeta("allods_template").AsString()
                .Contains(AiMarkerTemplate, StringComparison.OrdinalIgnoreCase));
        int partiallyUntexturedModels = rendered.Count(placement =>
            !placement.GetMeta("allods_template").AsString()
                .Contains(AiMarkerTemplate, StringComparison.OrdinalIgnoreCase)
            && MeshesBelow(placement).Any(mesh =>
                CountTexturedSurfaces(mesh) != (mesh.Mesh?.GetSurfaceCount() ?? 0)));

        bool passed = authored.Length == ExpectedPlacements
            && placements.Length == ExpectedPlacements
            && loader.PlacedObjectCount == ExpectedPlacements
            && loader.VisualObjectCount == ExpectedVisualPlacements
            && loader.NonVisualObjectCount == ExpectedNonVisualPlacements
            && loader.UnresolvedObjectCount == 0
            && loader.StaticModelFailures.Count == 0
            && rendered.Length == ExpectedVisualPlacements
            && sceneResolved == ExpectedVisualPlacements
            && geometryFallbacks == 0
            && textureless.Length == ExpectedTexturelessMarkers
            && unexpectedTextureless == 0
            && partiallyUntexturedModels == 0
            && transformMismatches == 0
            && invalidTransforms == 0
            && missingTemplates == 0
            && emptyMeshes == 0
            && unmaterialedMeshes == 0;

        GD.Print(
            $"STATIC_VISUAL_PROBE authored={authored.Length} placements={loader.PlacedObjectCount} "
            + $"visual={loader.VisualObjectCount} non_visual={loader.NonVisualObjectCount} "
            + $"unresolved={loader.UnresolvedObjectCount} rendered={rendered.Length} "
            + $"scene={sceneResolved} geometry_fallback={geometryFallbacks} "
            + $"textureless_markers={textureless.Length} unexpected_textureless={unexpectedTextureless} "
            + $"partially_untextured={partiallyUntexturedModels} transform_mismatches={transformMismatches} "
            + $"invalid_transforms={invalidTransforms} missing_templates={missingTemplates} "
            + $"empty_meshes={emptyMeshes} unmaterialed={unmaterialedMeshes} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        if (!passed && !string.IsNullOrWhiteSpace(loader.LastError))
        {
            GD.PushError(loader.LastError);
        }

        GetTree().Quit(passed ? 0 : 1);
    }

    private static IReadOnlyList<AuthoredPlacement> ReadAuthoredPlacements(ZoneLoader loader)
    {
        string mapRoot = $"{loader.ConvertedRoot.TrimEnd('/')}/assets/Maps/{loader.MapName}";
        var result = new List<AuthoredPlacement>();
        foreach (string path in EnumerateFiles(mapRoot)
                     .Where(path => path.EndsWith(MapRegionSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            Vector3 tileOrigin = ZoneLoader.TileOrigin(path);
            using JsonDocument document = JsonDocument.Parse(FileAccess.GetFileAsString(path));
            foreach (JsonElement placement in document.RootElement.GetProperty("objects").EnumerateArray())
            {
                float[] position = placement.GetProperty("position").EnumerateArray()
                    .Select(value => value.GetSingle()).ToArray();
                float[] rotation = placement.GetProperty("rotation_yaw_pitch_roll").EnumerateArray()
                    .Select(value => value.GetSingle()).ToArray();
                float scale = placement.GetProperty("scale").GetSingle();
                result.Add(new AuthoredPlacement(
                    placement.GetProperty("template_href").GetString() ?? string.Empty,
                    tileOrigin + new Vector3(position[0], position[2], -position[1]),
                    ConvertRotation(rotation),
                    Vector3.One * (scale <= 0 ? 1.0f : scale)));
            }
        }

        return result;
    }

    private static int CountTransformMismatches(AuthoredPlacement[] authored, Node3D[] instances)
    {
        if (authored.Length != instances.Length)
        {
            return Math.Max(authored.Length, instances.Length);
        }

        int mismatches = 0;
        for (int index = 0; index < authored.Length; index++)
        {
            AuthoredPlacement expected = authored[index];
            Node3D actual = instances[index];
            bool templateMatches = actual.GetMeta("allods_template", string.Empty).AsString()
                .Equals(expected.Template, StringComparison.Ordinal);
            bool positionMatches = actual.Position.IsEqualApprox(expected.Position);
            bool scaleMatches = actual.Scale.IsEqualApprox(expected.Scale);
            bool rotationMatches = MathF.Abs(actual.Quaternion.Dot(expected.Rotation)) >= 0.99999f;
            if (!templateMatches || !positionMatches || !scaleMatches || !rotationMatches)
            {
                mismatches++;
            }
        }

        return mismatches;
    }

    private static Quaternion ConvertRotation(float[] source)
    {
        var yaw = new Quaternion(Vector3.Up, source[0]);
        var pitch = new Quaternion(Vector3.Right, source[1]);
        var roll = new Quaternion(new Vector3(0, 0, -1), source[2]);
        return yaw * pitch * roll;
    }

    private static List<string> EnumerateFiles(string root)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directoryPath = pending.Pop();
            using DirAccess? directory = DirAccess.Open(directoryPath);
            if (directory == null)
            {
                continue;
            }

            directory.ListDirBegin();
            string name = directory.GetNext();
            while (!string.IsNullOrEmpty(name))
            {
                if (!name.StartsWith('.'))
                {
                    string path = $"{directoryPath}/{name}";
                    if (directory.CurrentIsDir())
                    {
                        pending.Push(path);
                    }
                    else
                    {
                        result.Add(path);
                    }
                }

                name = directory.GetNext();
            }

            directory.ListDirEnd();
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
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
            .All(surface => mesh.GetSurfaceOverrideMaterial(surface) == null
                && mesh.Mesh.SurfaceGetMaterial(surface) == null);
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

    private sealed record AuthoredPlacement(
        string Template,
        Vector3 Position,
        Quaternion Rotation,
        Vector3 Scale);
}
