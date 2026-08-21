using System;

namespace SarnautCore;

public partial class PartialNativeTerrainFallbackLoader : ZoneLoader
{
    protected override bool TryAddNativeTerrainTile(string terrainPath, out string error)
    {
        if (terrainPath.EndsWith("/1_2.terrain.tscn", StringComparison.OrdinalIgnoreCase))
        {
            error = "Injected native terrain failure for 1_2.";
            return false;
        }

        return base.TryAddNativeTerrainTile(terrainPath, out error);
    }
}

