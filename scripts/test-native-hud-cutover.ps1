$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

$retiredFiles = @(
    "src\ui\ConvertedHudChrome.cs",
    "src\ui\ConvertedHudChrome.cs.uid",
    "src\ui\ConvertedUiTranslations.cs",
    "src\ui\ConvertedUiTranslations.cs.uid",
    "src\ui\GameplayHudControl.cs",
    "src\ui\GameplayHudControl.cs.uid",
    "tests\GameplayHudConvertedLifecycleSmoke.cs",
    "tests\GameplayHudConvertedLifecycleSmoke.cs.uid",
    "tests\GameplayHudFallbackSmoke.cs",
    "tests\GameplayHudFallbackSmoke.cs.uid",
    "tests\GameplayHudLayoutSmoke.cs",
    "tests\GameplayHudLayoutSmoke.cs.uid",
    "tests\gameplay_hud_converted_lifecycle_smoke.tscn",
    "tests\gameplay_hud_fallback_smoke.tscn",
    "tests\gameplay_hud_layout_smoke.tscn"
)
foreach ($relativePath in $retiredFiles) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $relativePath)) {
        throw "retired HUD source remains: $relativePath"
    }
}

$theme = Get-Content -LiteralPath (Join-Path $projectRoot "src\ui\UiTheme.cs") -Raw
$zone = Get-Content -LiteralPath (Join-Path $projectRoot "src\zone\ZoneWalkabout.cs") -Raw
$network = Get-Content -LiteralPath (Join-Path $projectRoot "src\zone\ZoneNetworkLoop.cs") -Raw
$gate = Get-Content -LiteralPath (Join-Path $projectRoot "scripts\visual-gate.ps1") -Raw

foreach ($forbidden in @(
    "ConvertedHudChrome",
    "ConvertedUiTranslations",
    "GameplayHudControl",
    "gameplay_hud_layout_smoke",
    "sarnaut/ui/converted_theme"
)) {
    $matches = rg -n -S $forbidden `
        (Join-Path $projectRoot "src") `
        (Join-Path $projectRoot "scenes") `
        (Join-Path $projectRoot "project.godot") `
        (Join-Path $projectRoot "scripts\visual-gate.ps1") `
        --glob '*.cs' --glob '*.tscn' --glob '*.godot' --glob '*.ps1' 2>$null
    if ($LASTEXITCODE -eq 0 -or $matches) {
        throw "retired HUD contract '$forbidden' remains in product or gate:`n$matches"
    }
    if ($LASTEXITCODE -ne 1) {
        throw "rg failed while checking '$forbidden' with exit code $LASTEXITCODE"
    }
}

foreach ($forbiddenThemeDependency in @(
    "ProjectSettings",
    "ConvertedSceneLoader",
    "res://converted",
    "Allods"
)) {
    if ($theme.Contains($forbiddenThemeDependency, [StringComparison]::OrdinalIgnoreCase)) {
        throw "UiTheme still depends on retired content: $forbiddenThemeDependency"
    }
}

foreach ($required in @(
    "NativeGameplayHudHost.TryMount(",
    "NativeHudContentPaths.Canonical()",
    'GetNode<CanvasLayer>("Interface")'
)) {
    if (-not $zone.Contains($required, [StringComparison]::Ordinal)) {
        throw "ZoneWalkabout is missing native HUD contract: $required"
    }
}

foreach ($retiredRuntimePath in @(
    "GameplayHudViewModel",
    "EntityHudSnapshot",
    "RequestAbilityUse",
    "RequestLootTake",
    "RequestQuestAccept",
    "RequestQuestTurnIn",
    "RequestQuestAbandon"
)) {
    if ($zone.Contains($retiredRuntimePath, [StringComparison]::Ordinal) -or
        $network.Contains($retiredRuntimePath, [StringComparison]::Ordinal)) {
        throw "zone runtime still carries retired HUD path: $retiredRuntimePath"
    }
}

if (-not $gate.Contains('Scene = "native_hud_compiled_lifecycle_smoke"', [StringComparison]::Ordinal) -or
    -not $gate.Contains('NATIVE_HUD_COMPILED_LIFECYCLE message_boxes=2 action_slots=36 result=PASS', [StringComparison]::Ordinal)) {
    throw "visual gate is missing the compiled native HUD lifecycle proof"
}

Write-Output "native HUD cutover source test: PASS"
