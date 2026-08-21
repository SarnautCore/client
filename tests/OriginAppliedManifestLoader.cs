using System;

namespace SarnautCore;

/// <summary>Corrupts the aggregate native terrain frame through its read seam.</summary>
public partial class OriginAppliedManifestLoader : ZoneLoader
{
    protected override string ReadNativeTerrainManifestText(string manifestPath) =>
        base.ReadNativeTerrainManifestText(manifestPath).Replace(
            "\"origin_applied\": true",
            "\"origin_applied\": false",
            StringComparison.Ordinal);
}
