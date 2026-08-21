using System;

namespace SarnautCore;

public partial class UnrecoverableTerrainFailureLoader : ZoneLoader
{
    protected override string ReadNativeTerrainManifestText(string manifestPath) =>
        base.ReadNativeTerrainManifestText(manifestPath).Replace(
            "1_2/1_2_terrain.tscn",
            "0_2/0_2_terrain.tscn",
            StringComparison.Ordinal);
}
