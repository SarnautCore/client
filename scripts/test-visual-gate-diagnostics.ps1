$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "visual-gate-diagnostics.ps1")

function Assert-Reasons {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Stdout,

        [string]$Stderr = "",

        [string[]]$AllowedErrorPatterns = @(),

        [string[]]$RequiredStdoutPatterns = @(),

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedReasons
    )

    $actual = @(Get-VisualGateDiagnosticReasons `
        -Stdout $Stdout `
        -Stderr $Stderr `
        -AllowedErrorPatterns $AllowedErrorPatterns `
        -RequiredStdoutPatterns $RequiredStdoutPatterns)
    if ($actual.Count -ne $ExpectedReasons.Count) {
        throw "$Name expected $($ExpectedReasons.Count) reason(s), got $($actual.Count): $($actual -join '; ')"
    }

    for ($index = 0; $index -lt $ExpectedReasons.Count; $index++) {
        if ($actual[$index] -ne $ExpectedReasons[$index]) {
            throw "$Name reason $index expected '$($ExpectedReasons[$index])', got '$($actual[$index])'"
        }
    }
}

$expectedInjectedError = '^ERROR: ZoneLoader: expected injected failure\.$'
$originAppliedError = "ERROR: ZoneLoader: Native terrain manifest is incompatible: res://content/league-slice/maps/inst-league-start/terrain-manifest.json"
$partialTerrainError = "ERROR: ZoneLoader: Native terrain scene is missing: res://content/league-slice/maps/inst-league-start/1_2/missing_terrain.scn"
$unrecoverableTerrainError = "ERROR: ZoneLoader: Native terrain scene is listed more than once: res://content/league-slice/maps/inst-league-start/0_2/0_2_terrain.scn"
$fixtures = @(
    @{
        Name = "clean pass"
        Stdout = "PROBE value=1 result=PASS"
        ExpectedReasons = @()
    },
    @{
        Name = "required coverage present"
        Stdout = "PROBE cases=53 result=PASS"
        RequiredStdoutPatterns = @('(?:^|\s)cases=53(?:\s|$)')
        ExpectedReasons = @()
    },
    @{
        Name = "required coverage missing"
        Stdout = "PROBE cases=52 result=PASS"
        RequiredStdoutPatterns = @('(?:^|\s)cases=53(?:\s|$)')
        ExpectedReasons = @("stdout does not satisfy coverage requirement '(?:^|\s)cases=53(?:\s|$)'")
    },
    @{
        Name = "missing result"
        Stdout = "PROBE completed"
        ExpectedReasons = @("stdout does not report result=PASS")
    },
    @{
        Name = "failed result"
        Stdout = "PROBE result=FAIL"
        ExpectedReasons = @("stdout does not report result=PASS")
    },
    @{
        Name = "later failed result wins"
        Stdout = "PROBE stage=setup result=PASS`nPROBE result=FAIL"
        ExpectedReasons = @("stdout does not report result=PASS")
    },
    @{
        Name = "stderr error"
        Stdout = "PROBE result=PASS"
        Stderr = "ERROR: unexpected stderr diagnostic"
        ExpectedReasons = @("stderr reports unexpected ERROR lines")
    },
    @{
        Name = "stdout error after pass"
        Stdout = "PROBE result=PASS`nERROR: shutdown failed"
        ExpectedReasons = @("stdout reports unexpected ERROR lines")
    },
    @{
        Name = "prefixed stdout error"
        Stdout = "PROBE result=PASS`n[shutdown] ERROR: cleanup failed"
        ExpectedReasons = @("stdout reports unexpected ERROR lines")
    },
    @{
        Name = "stdout RID leak"
        Stdout = "PROBE result=PASS`nERROR: 1 RID allocations of type 'P12GodotShape3D' were leaked at exit."
        ExpectedReasons = @(
            "stdout reports leaked instances or resources",
            "stdout reports unexpected ERROR lines"
        )
    },
    @{
        Name = "stderr object leak"
        Stdout = "PROBE result=PASS"
        Stderr = "WARNING: ObjectDB instances leaked at exit."
        ExpectedReasons = @("stderr reports leaked instances or resources")
    },
    @{
        Name = "expected injected error"
        Stdout = "PROBE result=PASS"
        Stderr = "ERROR: ZoneLoader: expected injected failure."
        AllowedErrorPatterns = @($expectedInjectedError)
        ExpectedReasons = @()
    },
    @{
        Name = "origin-applied probe's exact injected error"
        Stdout = "ORIGIN_APPLIED_MANIFEST result=PASS"
        Stderr = $originAppliedError
        AllowedErrorPatterns = @(Get-VisualGateAllowedErrorPatterns -Scene "origin_applied_manifest_probe")
        ExpectedReasons = @()
    },
    @{
        Name = "unrecoverable-terrain probe's exact injected error"
        Stdout = "UNRECOVERABLE_TERRAIN_FAILURE result=PASS"
        Stderr = $unrecoverableTerrainError
        AllowedErrorPatterns = @(Get-VisualGateAllowedErrorPatterns -Scene "unrecoverable_terrain_failure_probe")
        ExpectedReasons = @()
    },
    @{
        Name = "partial-terrain probe's exact compiled-scene error"
        Stdout = "PARTIAL_NATIVE_TERRAIN_FORBIDDEN result=PASS"
        Stderr = $partialTerrainError
        AllowedErrorPatterns = @(Get-VisualGateAllowedErrorPatterns -Scene "partial_native_terrain_fallback_probe")
        ExpectedReasons = @()
    },
    @{
        Name = "unexpected error beside injected error"
        Stdout = "PROBE result=PASS"
        Stderr = "ERROR: ZoneLoader: expected injected failure.`nERROR: unrelated regression"
        AllowedErrorPatterns = @($expectedInjectedError)
        ExpectedReasons = @("stderr reports unexpected ERROR lines")
    },
    @{
        Name = "leak remains fatal for injected probe"
        Stdout = "PROBE result=PASS`nERROR: 1 RID allocations were leaked at exit."
        Stderr = "ERROR: ZoneLoader: expected injected failure."
        AllowedErrorPatterns = @($expectedInjectedError)
        ExpectedReasons = @(
            "stdout reports leaked instances or resources",
            "stdout reports unexpected ERROR lines"
        )
    }
)

foreach ($fixture in $fixtures) {
    $parameters = @{
        Name = $fixture.Name
        Stdout = $fixture.Stdout
        ExpectedReasons = [string[]]$fixture.ExpectedReasons
    }
    if ($fixture.ContainsKey("Stderr")) {
        $parameters.Stderr = $fixture.Stderr
    }
    if ($fixture.ContainsKey("AllowedErrorPatterns")) {
        $parameters.AllowedErrorPatterns = [string[]]$fixture.AllowedErrorPatterns
    }
    if ($fixture.ContainsKey("RequiredStdoutPatterns")) {
        $parameters.RequiredStdoutPatterns = [string[]]$fixture.RequiredStdoutPatterns
    }

    Assert-Reasons @parameters
}

$visualGate = Get-Content -LiteralPath (Join-Path $PSScriptRoot "visual-gate.ps1") -Raw
$probeCount = [regex]::Matches($visualGate, '@\{\s*Scene\s*=').Count
if ($probeCount -ne 17) {
    throw "visual gate must contain 17 probes, found $probeCount"
}

foreach ($requiredContract in @(
    'Scene = "converted_model_animation_smoke"',
    'CONVERTED_MODEL_ANIMATION cases=53',
    'Scene = "native_character_lod_smoke"',
    'SARNAUT_NATIVE_CHARACTER_LOD_ROOT = "res://content/league-slice"',
    'SARNAUT_NATIVE_CHARACTER_LOD_KEY = "*"',
    'NATIVE_CHARACTER_LOD identities=40/40',
    'Scene = "zone_presentation_pixel_probe"',
    'native_scene="res://content/league-slice/maps/inst-league-start/zones/inst-league1/.+\.scn"',
    'native_route=True manifest_exact=True topology_exact=True probe_colors_exact=True',
    'MaxSeconds = 45; MaxPeakBytes = 2469606195',
    '$process.PeakWorkingSet64',
    'visual-gate-metrics.json',
    '[Parameter(Mandatory = $true)]',
    'compiled-only gate content target mismatch',
    '$managedEnvironmentNames = @(',
    '[System.Environment]::SetEnvironmentVariable($name, $null, "Process")',
    '[System.Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], "Process")'
)) {
    if (-not $visualGate.Contains($requiredContract, [StringComparison]::Ordinal)) {
        throw "visual gate is missing standing coverage contract: $requiredContract"
    }
}

$zonePresentationProbe = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot "..\tests\ZonePresentationPixelProbe.cs") -Raw
foreach ($requiredPresentationValue in @(
    'maps/inst-league-start/zones/inst-league1/zone-presentation.json',
    'NativePresentationScenePath.EndsWith(".scn"',
    '["Backdrop", "Stars", "Clouds"]',
    '[0.8f, 0.4f, 1.0f]',
    'clip.xy *= fov_factor',
    'new Color(45.0f / 510.0f, 58.0f / 510.0f, 179.0f / 510.0f)',
    'new Color(70.0f / 255.0f, 30.0f / 255.0f, 0.0f)',
    'new Color(18.0f / 255.0f, 6.0f / 255.0f, 38.0f / 255.0f)',
    'new Vector3(0.0f, -45.0f, 0.0f)',
    '"blend_add"',
    '"blend_mix"',
    'new Color(6.0f / 255.0f, 57.0f / 255.0f, 119.0f / 255.0f)',
    'DirectionalLight3D.ShadowMode.Parallel4Splits'
)) {
    if (-not $zonePresentationProbe.Contains($requiredPresentationValue, [StringComparison]::Ordinal)) {
        throw "zone presentation probe is missing exact native contract: $requiredPresentationValue"
    }
}

$mountAssets = Get-Content -LiteralPath (Join-Path $PSScriptRoot "mount-assets.ps1") -Raw
foreach ($mountContract in @(
    '[string]$ContentRoot =',
    "`$existingItem.LinkType -ne 'Junction'",
    'junction target mismatch:',
    '-Target $ContentRoot',
    '-Required'
)) {
    if (-not $mountAssets.Contains($mountContract, [StringComparison]::Ordinal)) {
        throw "mount-assets is missing compiled-content mount contract: $mountContract"
    }
}

$zoneCameraProbe = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\tests\ZoneCameraSpawnSmoke.cs") -Raw
if ($zoneCameraProbe.Contains("1_2.terrain.up.obj", [StringComparison]::Ordinal) -or
    $zoneCameraProbe.Contains("ResourceLoader.Load<Mesh>", [StringComparison]::Ordinal) -or
    -not $zoneCameraProbe.Contains("compiledNativeTerrain", [StringComparison]::Ordinal) -or
    -not $zoneCameraProbe.Contains(".scn", [StringComparison]::Ordinal)) {
    throw "zone camera probe must derive its proof from compiled native terrain"
}

foreach ($faultLoader in @(
    "PartialNativeTerrainFallbackLoader.cs",
    "UnrecoverableTerrainFailureLoader.cs"
)) {
    $faultSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\tests\$faultLoader") -Raw
    if ($faultSource.Contains("_terrain.tscn", [StringComparison]::Ordinal) -or
        -not $faultSource.Contains("NativeTerrainManifestTestMutation", [StringComparison]::Ordinal)) {
        throw "$faultLoader must mutate authoritative compiled runtime_scene values"
    }
}

Write-Output "visual-gate diagnostics self-test: PASS ($($fixtures.Count) fixtures, $probeCount probes)"
