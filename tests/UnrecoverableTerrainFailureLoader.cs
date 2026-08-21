using System;
using System.Collections.Generic;

namespace SarnautCore;

public partial class UnrecoverableTerrainFailureLoader : ZoneLoader
{
    protected override bool TryAddNativeTerrainTile(string terrainPath, out string error)
    {
        if (IsInjectedTile(terrainPath))
        {
            error = "Injected native terrain failure for 1_2.";
            return false;
        }

        return base.TryAddNativeTerrainTile(terrainPath, out error);
    }

    protected override bool TryAddLegacyTerrainTile(
        string terrainPath,
        IReadOnlyList<string> layerTextures,
        out string error)
    {
        if (IsInjectedTile(terrainPath))
        {
            error = "Injected legacy terrain failure for 1_2.";
            return false;
        }

        return base.TryAddLegacyTerrainTile(terrainPath, layerTextures, out error);
    }

    private static bool IsInjectedTile(string terrainPath) =>
        terrainPath.Contains("/1_2.terrain.", StringComparison.OrdinalIgnoreCase);
}

