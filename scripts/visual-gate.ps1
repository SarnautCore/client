<#
.SYNOPSIS
    Runs the visual-completeness probe suite serially, one Godot at a time.

.DESCRIPTION
    The machine has a single serialized Godot slot, so every scene runs on its
    own process with stdout/stderr redirected to files (piping the console
    output hangs the editor binary). A probe fails the gate when its process
    exits non-zero, times out, omits result=PASS, or either output stream
    reports leaked instances or an unexpected ERROR line.

    player_appearance_pixel_probe saves its front/back PNG evidence only when
    SARNAUT_APPEARANCE_PROBE names a prefix, so the gate provides one under the
    output directory and gives that probe the largest budget.

.PARAMETER Godot
    Path to the Godot binary.

.PARAMETER OutputDirectory
    Where per-probe logs and pixel evidence land.
#>
param(
    [string]$Godot = "C:\Users\paulo\AppData\Local\Microsoft\WinGet\Links\godot_console.exe",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ($OutputDirectory.Length -eq 0) {
    $OutputDirectory = Join-Path $projectRoot ".cache\visual-gate"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
. (Join-Path $PSScriptRoot "visual-gate-diagnostics.ps1")

# Scene, timeout seconds, and any environment the probe needs.
$probes = @(
    @{ Scene = "authored_presentation_spawn_probe"; Timeout = 240 },
    @{ Scene = "canonical_player_grounding_probe"; Timeout = 240 },
    @{ Scene = "converted_model_animation_smoke"; Timeout = 300; RequiredStdoutPatterns = @(
            '(?m)^CONVERTED_MODEL_ANIMATION cases=53\s'
        )
    },
    @{ Scene = "directional_lighting_probe"; Timeout = 300 },
    @{ Scene = "gameplay_hud_converted_lifecycle_smoke"; Timeout = 240 },
    @{ Scene = "gameplay_hud_layout_smoke"; Timeout = 240 },
    @{ Scene = "live_zone_player_animation_probe"; Timeout = 240 },
    @{ Scene = "native_character_lod_smoke"; Timeout = 600; Environment = @{
            SARNAUT_NATIVE_CHARACTER_LOD_KEY = "*"
        }; RequiredStdoutPatterns = @(
            '(?m)^NATIVE_CHARACTER_LOD identities=40/40\s*$'
        )
    },
    @{ Scene = "online_coordinate_frame_smoke"; Timeout = 120 },
    @{ Scene = "origin_applied_manifest_probe"; Timeout = 240 },
    @{ Scene = "partial_native_terrain_fallback_probe"; Timeout = 240 },
    @{ Scene = "player_appearance_pixel_probe"; Timeout = 600; Environment = @{
            SARNAUT_APPEARANCE_PROBE = (Join-Path $OutputDirectory "player-appearance")
        }
    },
    @{ Scene = "static_visual_completeness_probe"; Timeout = 240 },
    @{ Scene = "terrain_structure_diagnostic_probe"; Timeout = 240 },
    @{ Scene = "unrecoverable_terrain_failure_probe"; Timeout = 240 },
    @{ Scene = "zone_camera_spawn_smoke"; Timeout = 240 },
    @{ Scene = "zone_presentation_pixel_probe"; Timeout = 400 }
)

$failures = @()
foreach ($probe in $probes) {
    $scene = $probe.Scene
    $stdout = Join-Path $OutputDirectory "$scene.stdout.log"
    $stderr = Join-Path $OutputDirectory "$scene.stderr.log"
    if ($probe.ContainsKey("Environment")) {
        foreach ($name in $probe.Environment.Keys) {
            [System.Environment]::SetEnvironmentVariable($name, $probe.Environment[$name], "Process")
        }
    }

    Write-Output ("== {0} (budget {1}s) ==" -f $scene, $probe.Timeout)
    $startParameters = @{
        FilePath = $Godot
        ArgumentList = @("--audio-driver", "Dummy", "--path", $projectRoot, "res://tests/$scene.tscn")
        WorkingDirectory = $projectRoot
        RedirectStandardOutput = $stdout
        RedirectStandardError = $stderr
        PassThru = $true
    }
    $process = Start-Process @startParameters
    # Cache the handle; without it Wait-Process leaves ExitCode null and every
    # probe fails the gate with a blank "exit code" reason.
    $null = $process.Handle
    $timedOut = $false
    try {
        Wait-Process -Id $process.Id -Timeout $probe.Timeout -ErrorAction Stop
    }
    catch {
        $timedOut = $true
        Stop-Process -Id $process.Id -Force -Confirm:$false -ErrorAction SilentlyContinue
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }

    if ($probe.ContainsKey("Environment")) {
        foreach ($name in $probe.Environment.Keys) {
            [System.Environment]::SetEnvironmentVariable($name, $null, "Process")
        }
    }

    $stdoutText = Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue
    if ($null -eq $stdoutText) { $stdoutText = "" }
    $stderrText = Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue
    if ($null -eq $stderrText) { $stderrText = "" }
    $reasons = @()
    if ($timedOut) { $reasons += "timed out after $($probe.Timeout)s" }
    elseif ($process.ExitCode -ne 0) { $reasons += "exit code $($process.ExitCode)" }
    $allowedErrorPatterns = @(Get-VisualGateAllowedErrorPatterns -Scene $scene)
    $requiredStdoutPatterns = if ($probe.ContainsKey("RequiredStdoutPatterns")) {
        @($probe.RequiredStdoutPatterns)
    }
    else {
        @()
    }
    $reasons += @(Get-VisualGateDiagnosticReasons `
        -Stdout $stdoutText `
        -Stderr $stderrText `
        -AllowedErrorPatterns $allowedErrorPatterns `
        -RequiredStdoutPatterns $requiredStdoutPatterns)

    if ($reasons.Count -gt 0) {
        $failures += "{0}: {1}" -f $scene, ($reasons -join "; ")
        Write-Output ("FAIL {0}: {1}" -f $scene, ($reasons -join "; "))
        $resultLine = Select-String -LiteralPath $stdout -Pattern "result=" -ErrorAction SilentlyContinue | Select-Object -Last 1
        if ($null -ne $resultLine) { Write-Output ("     {0}" -f $resultLine.Line) }
    }
    else {
        Write-Output ("PASS {0}" -f $scene)
    }
}

Write-Output ""
if ($failures.Count -gt 0) {
    Write-Output ("visual-gate: {0}/{1} probes failed" -f $failures.Count, $probes.Count)
    $failures | ForEach-Object { Write-Output ("  {0}" -f $_) }
    exit 1
}

Write-Output ("visual-gate: OK ({0} probes)" -f $probes.Count)
exit 0
