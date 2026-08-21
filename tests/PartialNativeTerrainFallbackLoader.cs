using System;

namespace SarnautCore;

public partial class PartialNativeTerrainFallbackLoader : ZoneLoader
{
    protected override string ReadNativeTerrainManifestText(string manifestPath) =>
        base.ReadNativeTerrainManifestText(manifestPath).Replace(
            "1_2/1_2_terrain.tscn",
            "1_2/missing_terrain.tscn",
            StringComparison.Ordinal);
}
