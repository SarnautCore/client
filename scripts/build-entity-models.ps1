<#
.SYNOPSIS
    Writes the entity model manifest the zone uses to bind a snapshot's
    content_id to a converted model.

.DESCRIPTION
    A snapshot names an entity by content id and nothing else (ADR 0007). The
    shard resolves those ids against the runtime pack; the client resolves them
    against the converted asset tree, and this writes the table that joins the
    two: content id to the mob's authored VisualMob href, which
    EntityModelCatalog then walks to a scene.

    The table is derived from extracted content, so it belongs with the
    converted assets and not in this repository. The output path is inside
    converted/, which is ignored by git; nothing here is committed and the
    client works without it, drawing labelled capsules instead.

.PARAMETER DataRepo
    The private data repository holding the mob documents.

.PARAMETER Ruleset
    The data ruleset directory to read, for example `classic`.

.PARAMETER ConvertedRoot
    The converted asset tree to write the manifest into.

.EXAMPLE
    ./scripts/build-entity-models.ps1 -DataRepo ..\data
#>
[CmdletBinding()]
param(
    [string]$DataRepo = "$PSScriptRoot/../../data",
    [string]$Ruleset = 'classic',
    [string]$ConvertedRoot = "$PSScriptRoot/../converted/assets/classic-1.1"
)

$ErrorActionPreference = 'Stop'

$zoneRoot = Join-Path $DataRepo "$Ruleset/zones"
if (-not (Test-Path $zoneRoot)) {
    throw "No zone documents at $zoneRoot. Point -DataRepo at the private data repository."
}

if (-not (Test-Path $ConvertedRoot)) {
    throw "No converted assets at $ConvertedRoot. Run the converter first, or pass -ConvertedRoot."
}

$models = [ordered]@{}
$skipped = 0
foreach ($document in Get-ChildItem -Path $zoneRoot -Recurse -Filter '*.yaml' -File) {
    $lines = Get-Content -LiteralPath $document.FullName
    $id = $null
    $visualRef = $null
    foreach ($line in $lines) {
        if ($line -match '^id:\s*(\S+)\s*$') { $id = $Matches[1] }
        elseif ($line -match '^visual_ref:\s*(\S+)\s*$') { $visualRef = $Matches[1] }
        elseif ($line -match '^\S' -and $id -and $visualRef) { break }
    }

    if (-not $id -or -not $id.StartsWith('mob.')) { continue }
    if (-not $visualRef) { $skipped++; continue }

    $models[$id] = [ordered]@{ visual_ref = $visualRef }
}

$manifest = [ordered]@{
    schema_version = 1
    ruleset        = $Ruleset
    models         = $models
}

$destination = Join-Path $ConvertedRoot 'entity_models.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $destination -Encoding utf8
Write-Host "Wrote $($models.Count) models to $destination ($skipped mobs had no visual_ref)."
