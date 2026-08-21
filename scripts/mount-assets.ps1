<#
.SYNOPSIS
    Mounts the converted, upscaled, and native content asset trees into the project.

.DESCRIPTION
    Asset trees are far too large to live in git, so the project reaches them
    through directory junctions:

        converted/assets -> <AssetRoot>\converted     (imported by Godot)
        upscaled/assets  -> <AssetRoot>\upscaled      (never imported)
        content/league-slice -> <ContentRoot>\league-slice (never imported)

    The upscaled and native content mounts carry .gdignore files. Godot therefore
    skips these subtrees during its filesystem scan. UpscaledTextures and
    NativeContentSettings read variants through FileAccess/ResourceLoader at
    runtime instead. Importing them would add tens of GB to .godot/imported for
    no rendering benefit.

    Idempotent: re-running only reports what is already in place.
#>
param(
    [string]$AssetRoot = 'E:\SarnautCore\assets'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

function Mount-Tree {
    param([string]$MountPoint, [string]$Target)

    if (-not (Test-Path $Target)) {
        Write-Warning "missing target $Target - skipping $MountPoint"
        return
    }

    $parent = Split-Path -Parent $MountPoint
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    if (Test-Path $MountPoint) {
        $existing = (Get-Item $MountPoint -Force).Target
        Write-Host "already mounted: $MountPoint -> $existing"
        return
    }

    New-Item -ItemType Junction -Path $MountPoint -Target $Target | Out-Null
    Write-Host "mounted: $MountPoint -> $Target"
}

Mount-Tree -MountPoint (Join-Path $projectRoot 'converted\assets') `
           -Target (Join-Path $AssetRoot 'converted')

Mount-Tree -MountPoint (Join-Path $projectRoot 'upscaled\assets') `
           -Target (Join-Path $AssetRoot 'upscaled')

# Mount native content (created by separate converter task).
$contentStagingRoot = Join-Path (Split-Path -Parent $AssetRoot) 'content-staging'
Mount-Tree -MountPoint (Join-Path $projectRoot 'content\league-slice') `
           -Target (Join-Path $contentStagingRoot 'league-slice')

# Must exist before Godot next scans, or it will start importing the 4x tree.
$gdignore = Join-Path $projectRoot 'upscaled\.gdignore'
if (-not (Test-Path $gdignore)) {
    Set-Content -Path $gdignore -Value '' -NoNewline
    Write-Host "wrote $gdignore"
}
else {
    Write-Host "already present: $gdignore"
}

# Must exist before Godot next scans for native content too.
$contentGdignore = Join-Path $projectRoot 'content\.gdignore'
if (-not (Test-Path $contentGdignore)) {
    Set-Content -Path $contentGdignore -Value '' -NoNewline
    Write-Host "wrote $contentGdignore"
}
else {
    Write-Host "already present: $contentGdignore"
}
