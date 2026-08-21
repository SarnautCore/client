using Godot;

namespace SarnautCore;

public partial class PartialNativeTerrainFallbackProbe : Node
{
    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        bool loaded = loader.LoadZone(ZoneLoader.DefaultMapName);
        Node3D terrain = loader.GetNode<Node3D>("Terrain");
        int tiles = terrain.GetChildCount();
        bool passed = !loaded
            && loader.TerrainTileCount == 0
            && loader.NativeTerrainTileCount == 0
            && tiles == 0
            && !loader.UsedFlatTerrainFallback
            && loader.LastError.Contains("Native terrain scene is missing", System.StringComparison.Ordinal)
            && loader.LastError.Contains("missing_terrain.tscn", System.StringComparison.Ordinal);

        GD.Print(
            $"PARTIAL_NATIVE_TERRAIN_FORBIDDEN loaded={loaded} terrain={loader.TerrainTileCount} "
            + $"native={loader.NativeTerrainTileCount} roots={tiles} "
            + $"flat_fallback={loader.UsedFlatTerrainFallback} "
            + $"error={loader.LastError} result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }
}
