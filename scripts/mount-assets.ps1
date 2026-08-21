<#
.SYNOPSIS
    Mounts the converted and upscaled asset trees into the project.

.DESCRIPTION
    Both trees are far too large to live in git, so the project reaches them
    through directory junctions:

        converted/assets -> <AssetRoot>\converted     (imported by Godot)
        upscaled/assets  -> <AssetRoot>\upscaled      (never imported)

    The upscaled mount carries a .gdignore. Godot therefore skips the whole
    subtree during its filesystem scan, and SarnautCore.UpscaledTextures reads
    the variants through FileAccess/Image at runtime instead. Importing them
    would add tens of GB to .godot/imported for no rendering benefit — see
    docs/upscaled-texture-preference.md.

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

# Must exist before Godot next scans, or it will start importing the 4x tree.
$gdignore = Join-Path $projectRoot 'upscaled\.gdignore'
if (-not (Test-Path $gdignore)) {
    Set-Content -Path $gdignore -Value '' -NoNewline
    Write-Host "wrote $gdignore"
}
else {
    Write-Host "already present: $gdignore"
}
