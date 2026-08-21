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
            && loader.TerrainTileCount == 3
            && loader.LastError.Contains("1 terrain tile(s) could not load", StringComparison.Ordinal)
            && loader.LastError.Contains("1_2", StringComparison.Ordinal)
            && loader.LastError.Contains("legacy fallback failed", StringComparison.Ordinal);

        GD.Print(
            $"UNRECOVERABLE_TERRAIN_FAILURE loaded={loaded} terrain={loader.TerrainTileCount} "
            + $"error={loader.LastError} result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }
}

