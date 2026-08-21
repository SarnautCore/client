$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$gate = Join-Path $PSScriptRoot "test-conversion-retirement.ps1"
if (-not (Test-Path -LiteralPath $gate -PathType Leaf)) {
    throw "conversion-retirement gate is missing: $gate"
}

function Set-FixtureFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Content
    )

    $path = Join-Path $Root $RelativePath
    $parent = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Set-Content -LiteralPath $path -Value $Content -NoNewline
}

function New-CleanFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    Set-FixtureFile $Root "src/ui/NativeGameplayHud.cs" @'
namespace Fixture;
public sealed class NativeGameplayHud { }
'@
    Set-FixtureFile $Root "src/session/SessionHost.cs" @'
namespace Fixture;
public static class SessionHost
{
    private const string CharacterSelect = "res://content/ui/character-select.scn";
    private const string CharacterCreate = "res://content/ui/character-create.scn";
}
'@
    Set-FixtureFile $Root "tests/NativeGameplayHudLifecycleSmoke.cs" @'
namespace Fixture;
public sealed class NativeGameplayHudLifecycleSmoke { }
'@
    Set-FixtureFile $Root "tests/native_gameplay_hud_lifecycle_smoke.tscn" @'
[gd_scene load_steps=2 format=3]
[ext_resource type="Script" path="res://tests/NativeGameplayHudLifecycleSmoke.cs" id="1"]
[node name="NativeGameplayHudLifecycleSmoke" type="Node"]
script = ExtResource("1")
'@
    Set-FixtureFile $Root "scripts/visual-gate.ps1" @'
$probes = @(
    @{ Scene = "native_gameplay_hud_lifecycle_smoke"; Timeout = 60 }
)
'@
    Set-FixtureFile $Root "scripts/mount-assets.ps1" @'
param([string]$ContentRoot)
Mount-Tree -MountPoint (Join-Path $projectRoot 'content/league-slice') -Target $ContentRoot -Required
'@
    Set-FixtureFile $Root "project.godot" @'
[sarnaut]
content/native_root="res://content/league-slice"
'@
    Set-FixtureFile $Root "SarnautCore.csproj" @'
<Project Sdk="Godot.NET.Sdk/4.7.2"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
'@
    Set-FixtureFile $Root ".gitignore" @'
.godot/
bin/
obj/
content/*
!content/.gdignore
'@

    # Product bytes and private/offline provenance deliberately retain source facts.
    # The runtime scanner must not inspect these trees.
    Set-FixtureFile $Root "content/league-slice/ui/login.scn" "baked upscaled product bytes"
    Set-FixtureFile $Root "content/league-slice/provenance.json" '{"allods_source":"private/input/Login.xdb","mesh":"body.skmesh","upscaled":true}'
    Set-FixtureFile $Root "offline/private-bake/provenance.json" '{"converter":"AllodsReader","source":"private/input/Login.xdb","path":"res://upscaled/assets"}'
}

function Invoke-Gate {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Audit", "Strict")]
        [string]$Mode
    )

    $output = & pwsh -NoProfile -File $gate -ProjectRoot $Root -Mode $Mode 2>&1 | Out-String
    return @{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$Expected
    )

    if (-not $Actual.Contains($Expected, [StringComparison]::Ordinal)) {
        throw "$Name expected output containing '$Expected', got:`n$Actual"
    }
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "sarnaut-conversion-retirement-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

try {
    $cleanRoot = Join-Path $fixtureRoot "clean"
    New-Item -ItemType Directory -Path $cleanRoot | Out-Null
    New-CleanFixture $cleanRoot

    $cleanStrict = Invoke-Gate $cleanRoot "Strict"
    if ($cleanStrict.ExitCode -ne 0) {
        throw "clean strict fixture failed with exit $($cleanStrict.ExitCode):`n$($cleanStrict.Output)"
    }
    Assert-Contains "clean strict" $cleanStrict.Output "CONVERSION_RETIREMENT_OK mode=Strict findings=0"

    $dirtyRoot = Join-Path $fixtureRoot "dirty"
    Copy-Item -LiteralPath $cleanRoot -Destination $dirtyRoot -Recurse
    Set-FixtureFile $dirtyRoot "src/characters/ConvertedSceneLoader.cs" @'
namespace Fixture;
public sealed class ConvertedSceneLoader
{
    private const string Source = "res://converted/assets/model.skmesh";
    private const string Metadata = "allods_skin_mesh";
    private const string Template = "legacy.xdb";
    public void Load() => UpscaledTextures.Load(Source);
}
public sealed class AllodsResourceReader { }
'@
    Set-FixtureFile $dirtyRoot "src/legacy/OriginalData.cs" 'public static class OriginalData { public const string Source = "legacy.xdb"; }'
    Set-FixtureFile $dirtyRoot "src/legacy/UpscaledPath.cs" 'public static class UpscaledPath { public const string Source = "res://upscaled/assets"; }'
    Set-FixtureFile $dirtyRoot "src/ui/GameplayHudControl.cs" "namespace Fixture; public sealed class GameplayHudControl { }"
    Set-FixtureFile $dirtyRoot "src/session/SessionHost.cs" 'private const string CharacterSelect = "res://scenes/ui/character_select.tscn";'
    Set-FixtureFile $dirtyRoot "scenes/ui/character_select.tscn" "[gd_scene format=3]"
    Set-FixtureFile $dirtyRoot "scenes/ui/character_create.tscn" "[gd_scene format=3]"
    Set-FixtureFile $dirtyRoot "tests/GameplayHudFallbackSmoke.cs" "public sealed class GameplayHudFallbackSmoke { }"
    Set-FixtureFile $dirtyRoot "tests/gameplay_hud_fallback_smoke.tscn" "[gd_scene format=3]"
    Set-FixtureFile $dirtyRoot "tests/ConvertedModelAnimationSmoke.cs" "public sealed class ConvertedModelAnimationSmoke { }"
    Set-FixtureFile $dirtyRoot "tests/converted_model_animation_smoke.tscn" "[gd_scene format=3]"
    Set-FixtureFile $dirtyRoot "scripts/build-entity-models.ps1" 'param([string]$ConvertedRoot = "../converted")'
    Set-FixtureFile $dirtyRoot "scripts/mount-assets.ps1" @'
Mount-Tree -MountPoint 'converted/assets' -Target 'private/converted'
Mount-Tree -MountPoint 'upscaled/assets' -Target 'private/upscaled'
Mount-Tree -MountPoint 'content/league-slice' -Target 'private/content'
'@
    Set-FixtureFile $dirtyRoot "project.godot" @'
[sarnaut]
visual/upscaled_root="res://upscaled/assets"
content/native_root="res://content/league-slice"
'@
    Set-FixtureFile $dirtyRoot "SarnautCore.csproj" @'
<Project><PropertyGroup><DefaultItemExcludes>$(DefaultItemExcludes);converted*/**</DefaultItemExcludes></PropertyGroup></Project>
'@
    Set-FixtureFile $dirtyRoot ".gitignore" @'
converted/*
!converted/.gdignore
upscaled/*
!upscaled/.gdignore
content/*
!content/.gdignore
'@
    Set-FixtureFile $dirtyRoot "scripts/visual-gate.ps1" @'
$probes = @(
    @{ Scene = "native_gameplay_hud_lifecycle_smoke"; Timeout = 60 },
    @{ Scene = "gameplay_hud_converted_lifecycle_smoke"; Timeout = 60 }
)
'@

    $dirtyAudit = Invoke-Gate $dirtyRoot "Audit"
    if ($dirtyAudit.ExitCode -ne 0) {
        throw "dirty audit fixture must report without blocking; exit $($dirtyAudit.ExitCode):`n$($dirtyAudit.Output)"
    }
    foreach ($rule in @(
        "RUNTIME_CONVERTED_TYPE",
        "RUNTIME_ALLODS_TYPE",
        "RUNTIME_UPSCALED_LOADER",
        "RUNTIME_CONVERTED_PATH",
        "RUNTIME_ORIGINAL_EXTENSION",
        "RUNTIME_ORIGINAL_METADATA",
        "OBSOLETE_HUD_RUNTIME",
        "OBSOLETE_RUNTIME_FILE",
        "OBSOLETE_TEST_PROBE",
        "OBSOLETE_BUILD_HELPER",
        "OBSOLETE_ASSET_MOUNT",
        "OBSOLETE_PROJECT_SETTING",
        "OBSOLETE_BUILD_EXCLUDE",
        "OBSOLETE_MOUNT_MARKER",
        "OBSOLETE_UI_SCENE",
        "OBSOLETE_VISUAL_GATE_PROBE",
        "STALE_CHARACTER_ROUTE"
    )) {
        Assert-Contains "dirty audit" $dirtyAudit.Output "[$rule]"
    }
    Assert-Contains "dirty audit upscaled path" $dirtyAudit.Output "[RUNTIME_CONVERTED_PATH] src/legacy/UpscaledPath.cs"
    Assert-Contains "dirty audit xdb" $dirtyAudit.Output "[RUNTIME_ORIGINAL_EXTENSION] src/legacy/OriginalData.cs"
    Assert-Contains "dirty audit" $dirtyAudit.Output "CONVERSION_RETIREMENT_AUDIT mode=Audit"

    $dirtyStrict = Invoke-Gate $dirtyRoot "Strict"
    if ($dirtyStrict.ExitCode -eq 0) {
        throw "dirty strict fixture unexpectedly passed:`n$($dirtyStrict.Output)"
    }
    Assert-Contains "dirty strict" $dirtyStrict.Output "CONVERSION_RETIREMENT_BLOCKED mode=Strict"

    $missingNativeRoot = Join-Path $fixtureRoot "missing-native"
    Copy-Item -LiteralPath $cleanRoot -Destination $missingNativeRoot -Recurse
    Remove-Item -LiteralPath (Join-Path $missingNativeRoot "tests/NativeGameplayHudLifecycleSmoke.cs")
    Set-FixtureFile $missingNativeRoot "scripts/visual-gate.ps1" '$probes = @()'

    $missingNative = Invoke-Gate $missingNativeRoot "Strict"
    if ($missingNative.ExitCode -eq 0) {
        throw "missing-native strict fixture unexpectedly passed:`n$($missingNative.Output)"
    }
    Assert-Contains "missing native" $missingNative.Output "[MISSING_NATIVE_HUD_PROBE]"
    Assert-Contains "missing native" $missingNative.Output "[MISSING_NATIVE_HUD_GATE]"

    Write-Output "conversion-retirement fixtures: PASS (clean, dirty audit/strict, missing-native)"
}
finally {
    $resolvedFixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedFixtureRoot.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        $resolvedFixtureRoot.Contains("sarnaut-conversion-retirement-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
