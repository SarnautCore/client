namespace SarnautCore;

public partial class PartialNativeTerrainFallbackLoader : ZoneLoader
{
    protected override string ReadNativeTerrainManifestText(string manifestPath) =>
        NativeTerrainManifestTestMutation.ReplaceCompiledSceneWithMissing(
            base.ReadNativeTerrainManifestText(manifestPath),
            "1_2");
}
