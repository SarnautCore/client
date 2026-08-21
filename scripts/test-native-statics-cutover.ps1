$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$zonePath = Join-Path $root "src\zone\ZoneLoader.cs"
$zone = Get-Content -LiteralPath $zonePath -Raw

$nativeStart = $zone.IndexOf("private bool TryIndexNativeStatics", [StringComparison]::Ordinal)
$nativeEnd = $zone.IndexOf("private bool TryLoadNativeCharacterPlacements", $nativeStart, [StringComparison]::Ordinal)
if ($nativeStart -lt 0 -or $nativeEnd -le $nativeStart) {
    throw "native static ZoneLoader section was not found"
}

$nativeSection = $zone.Substring($nativeStart, $nativeEnd - $nativeStart)
foreach ($pattern in @(
    "converted",
    "Allods",
    "\.xdb",
    "\.obj",
    "ConvertedSceneLoader",
    "UpscaledTextures",
    "LoadStaticPlacements",
    "ResolveStaticObject",
    "InstantiateGeometryFallback",
    "BakedStaticLighting",
    "AuthoredZoneLights"
)) {
    if ($nativeSection -match $pattern) {
        throw "native static runtime still contains retired pattern '$pattern'"
    }
}

foreach ($required in @(
    "NativeStaticBake.Parse",
    "Native static bake manifest is missing",
    "Native static scene is missing",
    "ConfigureNativeStaticLighting",
    "HasUsableNativeCollision",
    "AddStaticCollision",
    "_staticFatal"
)) {
    if ($zone.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "native static fail-closed boundary is missing '$required'"
    }
}

foreach ($retired in @(
    "src\zone\AuthoredZoneLights.cs",
    "src\zone\AuthoredZoneLights.cs.uid",
    "src\zone\BakedStaticLighting.cs",
    "src\zone\BakedStaticLighting.cs.uid"
)) {
    if (Test-Path -LiteralPath (Join-Path $root $retired)) {
        throw "retired converted static-lighting file still exists: $retired"
    }
}

foreach ($retained in @(
    "src\zone\BakedLightProbe.cs",
    "src\zone\SampledEntityLight.cs",
    "src\zone\DynamicEntityLighting.cs"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $retained))) {
        throw "shared native lighting capability was removed: $retained"
    }
}

$allProductSource = Get-ChildItem -LiteralPath (Join-Path $root "src") -Recurse -File -Filter "*.cs" |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
$joinedSource = $allProductSource -join "`n"
foreach ($retiredType in @("BakedStaticLighting", "AuthoredZoneLights", "AuthoredZoneLight")) {
    if ($joinedSource.IndexOf($retiredType, [StringComparison]::Ordinal) -ge 0) {
        throw "product source still references retired type '$retiredType'"
    }
}

Write-Output "native-statics-cutover: PASS"
