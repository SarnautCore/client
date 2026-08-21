$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$viewerPath = Join-Path $root "src\AssetViewer.cs"
$smokePath = Join-Path $root "tests\AssetViewerSmoke.cs"
$viewer = Get-Content -LiteralPath $viewerPath -Raw
$smoke = Get-Content -LiteralPath $smokePath -Raw
$ownedSource = $viewer + "`n" + $smoke

foreach ($required in @(
    "NativeContentSettings.NativeRoot",
    "NativeAssetReference.TryCreate",
    "NativeAssetKind.Scene",
    "NativeAssetKind.Resource",
    "ResourceLoader.Load"
)) {
    if ($ownedSource.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "native Asset Viewer is missing '$required'"
    }
}

foreach ($retired in @(
    ".skmesh",
    "res://converted",
    "ConvertedRoot",
    "AllodsResourceTree",
    "ConvertedSkinnedMesh",
    "ConvertedImporterMesh",
    "UpscaledTextures"
)) {
    if ($ownedSource.IndexOf($retired, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "native Asset Viewer still references '$retired'"
    }
}

foreach ($retained in @(
    "src\GodotNative\ConvertedSkinnedMesh.cs",
    "src\GodotNative\ConvertedImporterMesh.cs",
    "src\visual\UpscaledTextures.cs"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $retained))) {
        throw "shared runtime loader was removed by the Asset Viewer cutover: $retained"
    }
}

if (Test-Path -LiteralPath (Join-Path $root "src\zone\AllodsResourceTree.cs")) {
    throw "retired Allods resource tree remains after the native zone presentation cutover"
}

Write-Output "native-asset-viewer: PASS"
