using System.Linq;
using Godot;

namespace SarnautCore;

public partial class PartialNativeTerrainFallbackProbe : Node
{
    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        Node3D terrain = loader.GetNode<Node3D>("Terrain");
        Node3D[] tiles = terrain.GetChildren().OfType<Node3D>().ToArray();
        int nativeTiles = tiles.Count(tile => tile.GetMeta("layered_terrain", false).AsBool());
        int legacyTiles = tiles.Length - nativeTiles;
        bool passed = loader.TerrainTileCount == 4
            && tiles.Length == 4
            && nativeTiles == 3
            && legacyTiles == 1
            && loader.TerrainVertexCount > 0
            && !loader.UsedFlatTerrainFallback
            && string.IsNullOrWhiteSpace(loader.LastError);

        GD.Print(
            $"PARTIAL_NATIVE_TERRAIN_FALLBACK terrain={loader.TerrainTileCount} "
            + $"native={nativeTiles} legacy={legacyTiles} "
            + $"vertices={loader.TerrainVertexCount} flat_fallback={loader.UsedFlatTerrainFallback} "
            + $"error={loader.LastError} result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }
}

