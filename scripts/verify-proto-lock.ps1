<#
.SYNOPSIS
    Verifies src/SarnautCore.Network/Proto/PROTO_LOCK.sha256 against the tree.

.DESCRIPTION
    The lock is committed byte-identically in both repositories, so a
    hand-edited client proto is detectable without a server checkout — offline,
    and inside a release artifact (ADR 0027). Digests are taken over the file
    with CRLF normalised to LF so a checkout that ignores .gitattributes still
    computes what Linux CI computes.

    This does not replace scripts/sync-proto.ps1 -Check, which is the stronger
    assertion because it compares against the canonical tree rather than
    against a lock that could have been regenerated over a bad edit.
#>
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$protoRoot = Join-Path $repositoryRoot "src/SarnautCore.Network/Proto"
$lockPath = Join-Path $protoRoot "PROTO_LOCK.sha256"
if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    throw "PROTO_LOCK.sha256 is missing from $protoRoot."
}

$files = Get-ChildItem -LiteralPath $protoRoot -Recurse -File -Filter "*.proto" |
    ForEach-Object { [System.IO.Path]::GetRelativePath($protoRoot, $_.FullName).Replace("\", "/") } |
    Sort-Object -CaseSensitive
if ($files.Count -eq 0) {
    throw "No .proto files were found under $protoRoot."
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $builder = [System.Text.StringBuilder]::new()
    foreach ($relativePath in $files) {
        $bytes = [System.IO.File]::ReadAllBytes((Join-Path $protoRoot $relativePath))
        $text = [System.Text.Encoding]::UTF8.GetString($bytes).Replace("`r`n", "`n")
        $digest = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))
        $hex = [System.BitConverter]::ToString($digest).Replace("-", "").ToLowerInvariant()
        [void]$builder.Append("$hex  $relativePath`n")
    }
    $expected = $builder.ToString()
}
finally {
    $sha256.Dispose()
}

$actual = [System.IO.File]::ReadAllText($lockPath).Replace("`r`n", "`n")
if ($actual -ne $expected) {
    Write-Host "Committed PROTO_LOCK.sha256:" -ForegroundColor Red
    Write-Host $actual
    Write-Host "Recomputed from the proto tree:" -ForegroundColor Red
    Write-Host $expected
    throw "PROTO_LOCK.sha256 is stale. Re-run scripts/sync-proto.ps1 against a server checkout."
}

Write-Host "PROTO_LOCK.sha256 matches the client proto tree."
