$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$walkaboutPath = Join-Path $projectRoot "src\zone\ZoneWalkabout.cs"
$loaderPath = Join-Path $projectRoot "src\zone\ZoneLoader.cs"
$walkaboutScenePath = Join-Path $projectRoot "scenes\zone_walkabout.tscn"
$probePath = Join-Path $projectRoot "tests\ZonePresentationPixelProbe.cs"

$walkabout = Get-Content -LiteralPath $walkaboutPath -Raw
$loader = Get-Content -LiteralPath $loaderPath -Raw
$walkaboutScene = Get-Content -LiteralPath $walkaboutScenePath -Raw
$probe = Get-Content -LiteralPath $probePath -Raw

foreach ($required in @(
    'maps/{mapId}/zones/{zoneId}',
    'zone-presentation.json',
    'NativeZonePresentation.Parse(',
    'TryNormalizeContentId(mapName, out string mapId)',
    'ResourceLoader.Load<PackedScene>(scenePath)',
    'TryValidatePresentationTopology(',
    'new CameraCenteredSky { Name = "CameraCenteredSky" }',
    '_loader.ApplyZoneLighting(',
    'ApplyPresentationSpawn('
)) {
    if (-not $walkabout.Contains($required, [StringComparison]::Ordinal)) {
        throw "ZoneWalkabout is missing the native presentation contract: $required"
    }
}

foreach ($forbidden in @(
    'AllodsResourceTree',
    'ZoneEnvironmentSettings',
    'ZoneSkydome',
    'ConvertedRoot'
)) {
    if ($walkabout.Contains($forbidden, [StringComparison]::Ordinal) -or
        $loader.Contains($forbidden, [StringComparison]::Ordinal)) {
        throw "zone runtime still references retired conversion code: $forbidden"
    }
}

foreach ($retired in @(
    "src\zone\AllodsResourceTree.cs",
    "src\zone\ZoneEnvironment.cs",
    "src\zone\ZoneSkydome.cs",
    "addons\ao_converter\AllodsResource.cs",
    "addons\ao_converter\AllodsFmodProject.cs",
    "tests\P3RockDiagProbe.cs",
    "tests\p3_rock_diag.tscn"
)) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $retired)) {
        throw "retired conversion source remains: $retired"
    }
}

if ($walkaboutScene.Contains('type="WorldEnvironment"', [StringComparison]::Ordinal) -or
    $walkaboutScene.Contains('type="DirectionalLight3D"', [StringComparison]::Ordinal) -or
    $walkaboutScene.Contains('ConvertedRoot', [StringComparison]::Ordinal)) {
    throw "zone_walkabout.tscn still carries a presentation placeholder or converted root"
}

foreach ($requiredProbe in @(
    'maps/inst-league-start/zones/inst-league1/zone-presentation.json',
    'NativePresentationScenePath.EndsWith(".scn"',
    'manifest_exact={exactManifest}',
    'topology_exact={exactTopology}',
    'probe_colors_exact={exactProbeColors}'
)) {
    if (-not $probe.Contains($requiredProbe, [StringComparison]::Ordinal)) {
        throw "zone presentation probe is missing compiled-native proof: $requiredProbe"
    }
}

$legacyReferences = rg -n `
    'AllodsResourceTree|ZoneEnvironmentSettings|ZoneSkydome|AllodsResource|AllodsFmodProject' `
    (Join-Path $projectRoot "src") `
    (Join-Path $projectRoot "scenes") `
    --glob '*.cs' --glob '*.tscn' 2>$null
if ($LASTEXITCODE -eq 0 -or $legacyReferences) {
    throw "retired Allods conversion classes still have runtime references:`n$legacyReferences"
}
if ($LASTEXITCODE -ne 1) {
    throw "rg source-closure check failed with exit code $LASTEXITCODE"
}

Write-Output "native zone presentation cutover source test: PASS"
