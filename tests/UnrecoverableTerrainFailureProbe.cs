using System;
using Godot;

namespace SarnautCore;

public partial class UnrecoverableTerrainFailureProbe : Node
{
    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        bool loaded = loader.LoadZone(ZoneLoader.DefaultMapName);
        bool passed = !loaded
            && loader.TerrainTileCount == 0
            && loader.NativeTerrainTileCount == 0
            && !loader.UsedFlatTerrainFallback
            && loader.LastError.Contains("listed more than once", StringComparison.Ordinal)
            && loader.LastError.Contains("0_2_terrain.tscn", StringComparison.Ordinal);

        GD.Print(
            $"UNRECOVERABLE_TERRAIN_FAILURE loaded={loaded} terrain={loader.TerrainTileCount} "
            + $"error={loader.LastError} result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }
}
