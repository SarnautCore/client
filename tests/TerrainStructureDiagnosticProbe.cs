using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SarnautCore;

public partial class TerrainStructureDiagnosticProbe : Node
{
    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        Node3D terrain = loader.GetNode<Node3D>("Terrain");
        Node3D[] tiles = terrain.GetChildren().OfType<Node3D>().ToArray();
        int layered = tiles.Count(tile => HasShaderLayer(tile, "Up") && HasShaderLayer(tile, "Down"));
        int lit = tiles.Count(tile => HasLitLayer(tile, "Up") && HasLitLayer(tile, "Down"));
        var expectedOrigins = new Dictionary<string, Vector3>
        {
            ["0_2"] = new(0, 0, -5632),
            ["1_2"] = new(256, 0, -5632),
            ["1_3"] = new(256, 0, -5888),
            ["4_3"] = new(1024, 0, -5888),
        };
        int positioned = tiles.Count(tile =>
            expectedOrigins.TryGetValue(tile.Name, out Vector3 expected)
            && tile.Position.IsEqualApprox(expected));
        string[] expectedOrder = ["0_2", "1_2", "1_3", "4_3"];
        int ordered = tiles.Select(tile => tile.Name.ToString()).SequenceEqual(expectedOrder) ? 4 : 0;
        int manifestScenes = tiles.Count(tile =>
        {
            string path = tile.GetMeta("native_scene", string.Empty).AsString();
            return path.StartsWith(
                    $"{NativeContentSettings.NativeRoot}/maps/inst-league-start/",
                    StringComparison.Ordinal)
                && path.EndsWith($"/{tile.Name}_terrain.tscn", StringComparison.Ordinal)
                && !path.Contains("res://converted", StringComparison.OrdinalIgnoreCase);
        });
        bool globalBounds = loader.HasTerrainBounds
            && loader.TerrainBounds.Position.X >= 199.9f
            && loader.TerrainBounds.End.X >= 1239.9f
            && loader.TerrainBounds.Position.Z <= -6111.9f
            && loader.TerrainBounds.End.Z >= -5752.1f;
        // The League slice ships baked terrain (ADR 0040): the diagnostic gate
        // also proves every tile was served from the native content root. The
        // converted route stays covered by the fault-injection probes, which
        // force it explicitly.
        bool passed = loader.TerrainTileCount == 4
            && tiles.Length == 4
            && layered == 4
            && lit == 4
            && positioned == 4
            && ordered == 4
            && manifestScenes == 4
            && globalBounds
            && !loader.UsedFlatTerrainFallback
            && loader.NativeTerrainTileCount == 4;

        GD.Print(
            $"TERRAIN_STRUCTURE_DIAGNOSTIC reported={loader.TerrainTileCount} roots={tiles.Length} "
            + $"layered={layered} lit={lit} positioned={positioned} bounds={loader.TerrainBounds} "
            + $"ordered={ordered} manifest_scenes={manifestScenes} "
            + $"global_bounds={globalBounds} flat_fallback={loader.UsedFlatTerrainFallback} "
            + $"native={loader.NativeTerrainTileCount} "
            + $"result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private static bool HasShaderLayer(Node3D tile, string name)
    {
        MeshInstance3D? layer = tile.GetNodeOrNull<MeshInstance3D>(name);
        if (layer?.Mesh == null)
        {
            return false;
        }

        if (layer.MaterialOverride is ShaderMaterial)
        {
            return true;
        }

        return Enumerable.Range(0, layer.Mesh.GetSurfaceCount())
            .Any(surface => layer.GetActiveMaterial(surface) is ShaderMaterial);
    }

    private static bool HasLitLayer(Node3D tile, string name)
    {
        MeshInstance3D? layer = tile.GetNodeOrNull<MeshInstance3D>(name);
        ShaderMaterial? material = layer?.MaterialOverride as ShaderMaterial;
        string code = material?.Shader?.Code ?? string.Empty;
        // Rejects the converter's original sky-lit copy (render_mode unshaded,
        // cull_disabled) while accepting the client's baked two-term combine.
        // The render-mode directive is matched exactly so prose in comments
        // cannot trip the check.
        return layer?.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off
            && code.Contains("render_mode cull_back;")
            && !code.Contains("render_mode unshaded")
            && !code.Contains("cull_disabled")
            && code.Contains("baked_light")
            && code.Contains("ROUGHNESS = 1.0;")
            && code.Contains("SPECULAR = 0.0;");
    }
}
