using System;
using Godot;

namespace SarnautCore;

/// <summary>
/// The native aggregate must declare that every scene already carries its
/// world origin. A local-frame aggregate is rejected without a fallback.
/// </summary>
public partial class OriginAppliedManifestProbe : Node
{
    public override void _Ready()
    {
        ZoneLoader loader = GetNode<ZoneLoader>("ZoneLoader");
        bool loaded = loader.LoadZone(ZoneLoader.DefaultMapName);
        bool passed = !loaded
            && loader.TerrainTileCount == 0
            && loader.NativeTerrainTileCount == 0
            && !loader.UsedFlatTerrainFallback
            && loader.LastError.Contains("manifest is incompatible", StringComparison.Ordinal);

        GD.Print(
            $"ORIGIN_APPLIED_MANIFEST loaded={loaded} terrain={loader.TerrainTileCount} "
            + $"error={loader.LastError} result={(passed ? "PASS" : "FAIL")}");
        GetTree().Quit(passed ? 0 : 1);
    }
}
