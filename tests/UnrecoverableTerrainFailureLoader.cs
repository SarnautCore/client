namespace SarnautCore;

public partial class UnrecoverableTerrainFailureLoader : ZoneLoader
{
    protected override string ReadNativeTerrainManifestText(string manifestPath) =>
        NativeTerrainManifestTestMutation.DuplicateCompiledScene(
            base.ReadNativeTerrainManifestText(manifestPath),
            sourceTileId: "0_2",
            targetTileId: "1_2");
}
