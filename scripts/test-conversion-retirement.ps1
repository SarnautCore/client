<#
.SYNOPSIS
    Audits the client boundary after offline content materialization.

.DESCRIPTION
    Scans runtime and build inputs only. Baked product content and private/offline
    provenance are intentionally outside the scan: source extensions and provenance
    metadata are valid there, but the shipped client must not understand them.

    Audit mode reports remaining cutover work and exits successfully. Strict mode
    applies the same rules and exits non-zero until every finding is retired.
#>
param(
    [ValidateSet("Audit", "Strict")]
    [string]$Mode = "Audit",

    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path.TrimEnd('\', '/')
$findings = [Collections.Generic.List[object]]::new()

function Get-ProjectPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath.Equals($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return "."
    }

    $prefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "path escapes project root: $fullPath"
    }

    return $fullPath.Substring($prefix.Length).Replace('\', '/')
}

function Add-Finding {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Rule,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [int]$Line = 0,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $findings.Add([pscustomobject]@{
        Rule = $Rule
        Path = $Path
        Line = $Line
        Message = $Message
    })
}

function Get-LineNumber {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [int]$Index
    )

    if ($Index -le 0) {
        return 1
    }
    return ([regex]::Matches($Content.Substring(0, $Index), "`n").Count + 1)
}

function Find-Pattern {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Rule,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $match = [regex]::Match(
        $Content,
        $Pattern,
        [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if ($match.Success) {
        Add-Finding $Rule $Path (Get-LineNumber $Content $match.Index) $Message
    }
}

function Read-ProjectFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $path = Join-Path $resolvedRoot $RelativePath
    if (-not [IO.File]::Exists($path)) {
        return $null
    }
    return Get-Content -LiteralPath $path -Raw
}

# Runtime source is deliberately narrow. Never recurse through content, converted,
# upscaled, tests, documentation, caches, or offline/private bake workspaces.
$runtimeExtensions = @(".cs", ".gd", ".tscn", ".tres")
$runtimeFiles = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($relativeRoot in @("src", "scenes")) {
    $sourceRoot = Join-Path $resolvedRoot $relativeRoot
    if (-not [IO.Directory]::Exists($sourceRoot)) {
        continue
    }

    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
        if ($runtimeExtensions -contains $file.Extension.ToLowerInvariant()) {
            $runtimeFiles.Add($file)
        }
    }
}

$runtimeRules = @(
    @{
        Rule = "RUNTIME_CONVERTED_TYPE"
        Pattern = '\bConverted[A-Z][A-Za-z0-9_]*\b'
        Message = "runtime still names a Converted* conversion-layer symbol"
    },
    @{
        Rule = "RUNTIME_ALLODS_TYPE"
        Pattern = '\bAllods[A-Z][A-Za-z0-9_]*\b'
        Message = "runtime still names an Allods* source-reader symbol"
    },
    @{
        Rule = "RUNTIME_UPSCALED_LOADER"
        Pattern = '\bUpscaledTextures\b'
        Message = "runtime still references the obsolete upscaled-texture loader"
    },
    @{
        Rule = "RUNTIME_CONVERTED_PATH"
        Pattern = 'res://(?:converted|upscaled)(?:[/\\]|\b)'
        Message = "runtime still addresses a converted/upscaled source mount"
    },
    @{
        Rule = "RUNTIME_ORIGINAL_EXTENSION"
        Pattern = '\.(?:skmesh|xdb)\b'
        Message = "runtime still knows an original-source file extension"
    },
    @{
        Rule = "RUNTIME_ORIGINAL_METADATA"
        Pattern = '\ballods_[a-z0-9_]+\b'
        Message = "runtime still knows original-format metadata"
    },
    @{
        Rule = "OBSOLETE_AUTHORED_LIGHTS"
        Pattern = '\bauthored_lights\b|["'']AuthoredLights["'']'
        Message = "client build/runtime still carries the pre-materialization authored-lights branch"
    }
)

foreach ($file in $runtimeFiles) {
    $relativePath = Get-ProjectPath $file.FullName
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($rule in $runtimeRules) {
        Find-Pattern $rule.Rule $relativePath $content $rule.Pattern $rule.Message
    }
}

$obsoleteRuntimeFiles = @(
    "src/characters/ConvertedCharacter.cs",
    "src/characters/ConvertedSceneLoader.cs",
    "src/GodotNative/ConvertedImporterMesh.cs",
    "src/GodotNative/ConvertedSkinnedMesh.cs",
    "src/ui/ConvertedChrome.cs",
    "src/visual/UpscaledTextures.cs"
)
foreach ($relativePath in $obsoleteRuntimeFiles) {
    if ([IO.File]::Exists((Join-Path $resolvedRoot $relativePath))) {
        Add-Finding "OBSOLETE_RUNTIME_FILE" $relativePath 0 "obsolete conversion-layer runtime file remains"
    }
}
foreach ($relativePath in @(
    "src/ui/ConvertedHudChrome.cs",
    "src/ui/ConvertedUiTranslations.cs",
    "src/ui/GameplayHudControl.cs"
)) {
    if ([IO.File]::Exists((Join-Path $resolvedRoot $relativePath))) {
        Add-Finding "OBSOLETE_HUD_RUNTIME" $relativePath 0 "obsolete converted HUD runtime file remains"
    }
}

$obsoleteScenes = @(
    "scenes/ui/character_select.tscn",
    "scenes/ui/character_create.tscn"
)
foreach ($relativePath in $obsoleteScenes) {
    if ([IO.File]::Exists((Join-Path $resolvedRoot $relativePath))) {
        Add-Finding "OBSOLETE_UI_SCENE" $relativePath 0 "old ConvertedChrome character-flow scene remains"
    }
}

$obsoleteProbeFiles = @(
    "tests/GameplayHudConvertedLifecycleSmoke.cs",
    "tests/gameplay_hud_converted_lifecycle_smoke.tscn",
    "tests/GameplayHudFallbackSmoke.cs",
    "tests/gameplay_hud_fallback_smoke.tscn",
    "tests/GameplayHudLayoutSmoke.cs",
    "tests/gameplay_hud_layout_smoke.tscn",
    "tests/ConvertedIgnoredTreeLoadProbe.cs",
    "tests/converted_ignored_tree_load_probe.tscn",
    "tests/ConvertedModelAnimationSmoke.cs",
    "tests/converted_model_animation_smoke.tscn",
    "tests/UpscaledTextureProbe.cs",
    "tests/upscaled_texture_probe.tscn"
)
foreach ($relativePath in $obsoleteProbeFiles) {
    if ([IO.File]::Exists((Join-Path $resolvedRoot $relativePath))) {
        Add-Finding "OBSOLETE_TEST_PROBE" $relativePath 0 "conversion-layer probe remains after its native replacement"
    }
}

$sessionHost = Read-ProjectFile "src/session/SessionHost.cs"
if ($null -ne $sessionHost) {
    $parameters = @{
        Rule = "STALE_CHARACTER_ROUTE"
        Path = "src/session/SessionHost.cs"
        Content = $sessionHost
        Pattern = 'res://scenes/ui/character_(?:select|create)\.tscn'
        Message = "SessionHost still routes character flow through old ConvertedChrome scenes"
    }
    Find-Pattern @parameters
}

$visualGate = Read-ProjectFile "scripts/visual-gate.ps1"
$nativeGateKey = "native_gameplay_hud_lifecycle_smoke"
if ($null -eq $visualGate -or
    -not $visualGate.Contains($nativeGateKey, [StringComparison]::Ordinal)) {
    Add-Finding "MISSING_NATIVE_HUD_GATE" "scripts/visual-gate.ps1" 0 "visual gate lacks native_gameplay_hud_lifecycle_smoke"
}
if ($null -ne $visualGate) {
    foreach ($oldKey in @(
        "gameplay_hud_converted_lifecycle_smoke",
        "gameplay_hud_fallback_smoke",
        "gameplay_hud_layout_smoke",
        "converted_model_animation_smoke",
        "converted_ignored_tree_load_probe",
        "upscaled_texture_probe"
    )) {
        if ($visualGate.Contains($oldKey, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Finding "OBSOLETE_VISUAL_GATE_PROBE" "scripts/visual-gate.ps1" 0 "visual gate still selects '$oldKey'"
        }
    }
}

foreach ($relativePath in @(
    "tests/NativeGameplayHudLifecycleSmoke.cs",
    "tests/native_gameplay_hud_lifecycle_smoke.tscn"
)) {
    if (-not [IO.File]::Exists((Join-Path $resolvedRoot $relativePath))) {
        Add-Finding "MISSING_NATIVE_HUD_PROBE" $relativePath 0 "required native HUD lifecycle probe is missing"
    }
}
$nativeHudSource = Read-ProjectFile "tests/NativeGameplayHudLifecycleSmoke.cs"
if ($null -ne $nativeHudSource -and
    -not $nativeHudSource.Contains("NativeGameplayHudLifecycleSmoke", [StringComparison]::Ordinal)) {
    Add-Finding "INVALID_NATIVE_HUD_PROBE" "tests/NativeGameplayHudLifecycleSmoke.cs" 0 "native HUD probe source lacks its lifecycle probe type"
}
$nativeHudScene = Read-ProjectFile "tests/native_gameplay_hud_lifecycle_smoke.tscn"
if ($null -ne $nativeHudScene -and
    -not $nativeHudScene.Contains("res://tests/NativeGameplayHudLifecycleSmoke.cs", [StringComparison]::Ordinal)) {
    Add-Finding "INVALID_NATIVE_HUD_PROBE" "tests/native_gameplay_hud_lifecycle_smoke.tscn" 0 "native HUD probe scene does not bind its lifecycle probe source"
}

$buildHelper = "scripts/build-entity-models.ps1"
if ([IO.File]::Exists((Join-Path $resolvedRoot $buildHelper))) {
    Add-Finding "OBSOLETE_BUILD_HELPER" $buildHelper 0 "runtime-era entity conversion helper remains in the client"
}

$mountAssets = Read-ProjectFile "scripts/mount-assets.ps1"
if ($null -ne $mountAssets) {
    foreach ($mountName in @("converted", "upscaled")) {
        $parameters = @{
            Rule = "OBSOLETE_ASSET_MOUNT"
            Path = "scripts/mount-assets.ps1"
            Content = $mountAssets
            Pattern = ('(?m)(?:Mount-Tree|MountPoint|Join-Path)[^\r\n]*[''"]{0}(?:[/\\]|[''"])' -f [regex]::Escape($mountName))
            Message = "mount-assets must mount compiled content only; '$mountName' mount remains"
        }
        Find-Pattern @parameters
    }
}

$projectSettings = Read-ProjectFile "project.godot"
if ($null -ne $projectSettings) {
    $parameters = @{
        Rule = "OBSOLETE_PROJECT_SETTING"
        Path = "project.godot"
        Content = $projectSettings
        Pattern = '(?:visual/prefer_upscaled_textures|visual/upscaled_(?:root|mipmaps|vram_compression)|res://upscaled)'
        Message = "obsolete runtime upscaling setting remains"
    }
    Find-Pattern @parameters
}

$projectFiles = [Collections.Generic.List[IO.FileInfo]]::new()
$rootProject = Join-Path $resolvedRoot "SarnautCore.csproj"
if ([IO.File]::Exists($rootProject)) {
    $projectFiles.Add((Get-Item -LiteralPath $rootProject))
}
$sourceTree = Join-Path $resolvedRoot "src"
if ([IO.Directory]::Exists($sourceTree)) {
    foreach ($file in Get-ChildItem -LiteralPath $sourceTree -Recurse -File -Filter "*.csproj") {
        $projectFiles.Add($file)
    }
}
foreach ($file in $projectFiles) {
    $relativePath = Get-ProjectPath $file.FullName
    $content = Get-Content -LiteralPath $file.FullName -Raw
    $parameters = @{
        Rule = "OBSOLETE_BUILD_EXCLUDE"
        Path = $relativePath
        Content = $content
        Pattern = '(?:DefaultItemExcludes|Compile\s+Remove=)[^<>]*(?:converted|upscaled)'
        Message = "build configuration still special-cases a source mount"
    }
    Find-Pattern @parameters
}

$gitignore = Read-ProjectFile ".gitignore"
if ($null -ne $gitignore) {
    $parameters = @{
        Rule = "OBSOLETE_MOUNT_MARKER"
        Path = ".gitignore"
        Content = $gitignore
        Pattern = '(?m)^!?(?:converted|upscaled)(?:/|\\)'
        Message = ".gitignore still carries a converted/upscaled mount marker"
    }
    Find-Pattern @parameters
}
foreach ($relativePath in @(
    "converted/.gitkeep",
    "converted/.gdignore",
    "upscaled/.gdignore"
)) {
    if ([IO.File]::Exists((Join-Path $resolvedRoot $relativePath))) {
        Add-Finding "OBSOLETE_MOUNT_MARKER" $relativePath 0 "tracked converted/upscaled mount marker remains"
    }
}

$orderedFindings = @($findings | Sort-Object Rule, Path, Line)
foreach ($finding in $orderedFindings) {
    $location = $finding.Path
    if ($finding.Line -gt 0) {
        $location += ":$($finding.Line)"
    }
    Write-Output "[$($finding.Rule)] $location - $($finding.Message)"
}

if ($Mode -eq "Audit") {
    Write-Output "CONVERSION_RETIREMENT_AUDIT mode=Audit findings=$($orderedFindings.Count) strict_would_block=$($orderedFindings.Count -gt 0)"
    return
}

if ($orderedFindings.Count -gt 0) {
    Write-Output "CONVERSION_RETIREMENT_BLOCKED mode=Strict findings=$($orderedFindings.Count)"
    exit 1
}

Write-Output "CONVERSION_RETIREMENT_OK mode=Strict findings=0"
