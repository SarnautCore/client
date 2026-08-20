<#
.SYNOPSIS
    Copies the canonical .proto tree and its lock from the server repository.

.DESCRIPTION
    server/proto/sarnaut/v1 is canonical and every wire change is a server pull
    request first (ADR 0027). This client tree is a copy, never an edit site:
    the client has no committed generated code, so a hand-edited proto here
    builds green and mis-parses against a live shard instead of failing.

    The copies are byte-identical to the server's, because
    PROTO_LOCK.sha256 is digested over those bytes and the two repositories
    commit the same lock. The do-not-edit notice therefore lives in
    src/SarnautCore.Network/Proto/README.md rather than in a header the digests
    would not survive.

.PARAMETER ServerRepo
    Path to a server checkout. Defaults to the sibling ../server.

.PARAMETER Check
    Verify only. Writes nothing and exits non-zero if any copy would change.
#>
param(
    [string]$ServerRepo,
    [switch]$Check
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ServerRepo)) {
    $ServerRepo = Join-Path (Split-Path -Parent $repositoryRoot) "server"
}
if (-not (Test-Path -LiteralPath $ServerRepo -PathType Container)) {
    throw "No server checkout at $ServerRepo. Pass -ServerRepo <path>."
}

$sourceRoot = Join-Path (Resolve-Path $ServerRepo).Path "proto"
$sourceProtoRoot = Join-Path $sourceRoot "sarnaut/v1"
$targetRoot = Join-Path $repositoryRoot "src/SarnautCore.Network/Proto"
$targetProtoRoot = Join-Path $targetRoot "sarnaut/v1"
if (-not (Test-Path -LiteralPath $sourceProtoRoot -PathType Container)) {
    throw "No proto tree at $sourceProtoRoot."
}

$differences = [System.Collections.Generic.List[string]]::new()

function Sync-File {
    param([string]$Source, [string]$Target, [string]$Label)

    $sourceBytes = [System.IO.File]::ReadAllBytes($Source)
    # Not $matches: that is a PowerShell automatic variable and assigning to it
    # silently discards the last regex capture set for the rest of the scope.
    $identical = $false
    if (Test-Path -LiteralPath $Target -PathType Leaf) {
        $targetBytes = [System.IO.File]::ReadAllBytes($Target)
        $identical = [System.Linq.Enumerable]::SequenceEqual($sourceBytes, $targetBytes)
    }
    if ($identical) {
        return
    }

    if ($Check) {
        $differences.Add("differs: $Label")
        return
    }

    $parent = Split-Path -Parent $Target
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [System.IO.File]::WriteAllBytes($Target, $sourceBytes)
    Write-Host "copied $Label"
}

# Recursive, because both lock scripts hash the tree recursively. A proto added
# at sarnaut/v1/<subdir>/x.proto changes the server's lock, and a
# non-recursive copy would leave the client's tree without it while the client's
# own self-check still passed — the two locks would diverge with no error until
# somebody ran the cross-repo check.
function Get-RelativeProtoNames {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }
    $prefix = (Resolve-Path -LiteralPath $Root).Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    return Get-ChildItem -LiteralPath $Root -File -Filter "*.proto" -Recurse |
        ForEach-Object { $_.FullName.Substring($prefix.Length + 1).Replace([System.IO.Path]::DirectorySeparatorChar, "/") } |
        Sort-Object -CaseSensitive
}

$sourceNames = @(Get-RelativeProtoNames -Root $sourceProtoRoot)

foreach ($name in $sourceNames) {
    Sync-File `
        -Source (Join-Path $sourceProtoRoot $name) `
        -Target (Join-Path $targetProtoRoot $name) `
        -Label "sarnaut/v1/$name"
}

Sync-File `
    -Source (Join-Path $sourceRoot "PROTO_LOCK.sha256") `
    -Target (Join-Path $targetRoot "PROTO_LOCK.sha256") `
    -Label "PROTO_LOCK.sha256"

# A proto that no longer exists upstream is deleted rather than left behind: a
# stale message the shard has forgotten is worse than a missing one, because it
# still compiles.
foreach ($existing in Get-RelativeProtoNames -Root $targetProtoRoot) {
    if ($sourceNames -contains $existing) {
        continue
    }
    if ($Check) {
        $differences.Add("stale: sarnaut/v1/$existing")
        continue
    }
    Remove-Item -LiteralPath (Join-Path $targetProtoRoot $existing) -Force
    Write-Host "removed sarnaut/v1/$existing"
}

if ($Check) {
    if ($differences.Count -gt 0) {
        foreach ($difference in $differences) {
            Write-Host $difference -ForegroundColor Red
        }
        throw "The client proto tree does not match $sourceProtoRoot. Run scripts/sync-proto.ps1."
    }
    Write-Host "The client proto tree matches $sourceProtoRoot."
}
