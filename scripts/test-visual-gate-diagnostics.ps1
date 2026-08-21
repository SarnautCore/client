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
$originAppliedError = "ERROR: ZoneLoader: 1 terrain tile(s) could not load. res://converted/assets/classic-1.1/assets/Maps/Inst_LeagueStart/000_020/1_2: Tile-local coordinate contract is incompatible or already shifted: res://converted/assets/classic-1.1/assets/Maps/Inst_LeagueStart/000_020/1_2.terrain.tscn; legacy fallback failed: Injected: legacy fallback disabled for the origin-applied tile."
$unrecoverableTerrainError = "ERROR: ZoneLoader: 1 terrain tile(s) could not load. res://converted/assets/classic-1.1/assets/Maps/Inst_LeagueStart/000_020/1_2: Injected native terrain failure for 1_2.; legacy fallback failed: Injected legacy terrain failure for 1_2."
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
    'SARNAUT_NATIVE_CHARACTER_LOD_KEY = "*"',
    'NATIVE_CHARACTER_LOD identities=40/40'
)) {
    if (-not $visualGate.Contains($requiredContract, [StringComparison]::Ordinal)) {
        throw "visual gate is missing standing coverage contract: $requiredContract"
    }
}

Write-Output "visual-gate diagnostics self-test: PASS ($($fixtures.Count) fixtures, $probeCount probes)"
