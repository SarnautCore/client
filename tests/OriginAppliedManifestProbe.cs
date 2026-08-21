using System;
using Godot;

namespace SarnautCore;

/// <summary>
/// ADR 0038 gate item 5: a tile whose coordinate manifest claims
/// <c>origin_applied: true</c> has already been shifted, and drawing it again
/// would double-apply the origin. The zone load must fail, not shrug.
/// </summary>
public partial class OriginAppliedManifestProbe : Node
{
    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        bool loaded = loader.LoadZone(ZoneLoader.DefaultMapName);
        bool passed = !loaded
            && loader.TerrainTileCount == 3
            && loader.LastError.Contains("terrain tile(s) could not load", StringComparison.Ordinal)
            && loader.LastError.Contains("1_2", StringComparison.Ordinal)
            && loader.LastError.Contains("already shifted", StringComparison.Ordinal);

        GD.Print(
            $"ORIGIN_APPLIED_MANIFEST loaded={loaded} terrain={loader.TerrainTileCount} "
            + $"error={loader.LastError} result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }
}
