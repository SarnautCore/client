$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$zonePath = Join-Path $root "src\zone\ZoneLoader.cs"
$zone = Get-Content -LiteralPath $zonePath -Raw

$terrainStart = $zone.IndexOf("protected virtual bool TryLoadNativeTerrain", [StringComparison]::Ordinal)
$terrainEnd = $zone.IndexOf("private bool TryIndexNativeStatics", $terrainStart, [StringComparison]::Ordinal)
if ($terrainStart -lt 0 -or $terrainEnd -le $terrainStart) {
    throw "native terrain ZoneLoader section was not found"
}

$terrainSection = $zone.Substring($terrainStart, $terrainEnd - $terrainStart)
foreach ($pattern in @(
    "converted",
    "legacy",
    "Allods",
    "\.obj",
    "\.xdb",
    "ConvertedSceneLoader",
    "LoadTerrainTiles",
    "GeometryFallback"
)) {
    if ($terrainSection -match $pattern) {
        throw "native terrain runtime still contains retired pattern '$pattern'"
    }
}

foreach ($required in @(
    "terrain-manifest.json",
    "NativeSceneReference.Select",
    "NativeSceneReference.Extension",
    "entry.RuntimeScene",
    "ResourceLoader.Load<PackedScene>",
    "DisposePackedScenes",
    "Native terrain scene is missing",
    "_terrainFatal"
)) {
    if ($zone.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "native terrain fail-closed boundary is missing '$required'"
    }
}

if ($zone.IndexOf('[JsonPropertyName("runtime_scene")]', [StringComparison]::Ordinal) -lt 0) {
    throw "native terrain manifest DTO has no compiled runtime_scene field"
}

Write-Output "native-terrain-cutover: PASS"
