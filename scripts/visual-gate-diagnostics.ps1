Set-StrictMode -Version Latest

function Get-VisualGateAllowedErrorPatterns {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Scene
    )

    switch ($Scene) {
        "origin_applied_manifest_probe" {
            '^ERROR: ZoneLoader: 1 terrain tile\(s\) could not load\. res://converted/assets/classic-1\.1/assets/Maps/Inst_LeagueStart/000_020/1_2: Tile-local coordinate contract is incompatible or already shifted: res://converted/assets/classic-1\.1/assets/Maps/Inst_LeagueStart/000_020/1_2\.terrain\.tscn; legacy fallback failed: Injected: legacy fallback disabled for the origin-applied tile\.$'
        }
        "unrecoverable_terrain_failure_probe" {
            '^ERROR: ZoneLoader: 1 terrain tile\(s\) could not load\. res://converted/assets/classic-1\.1/assets/Maps/Inst_LeagueStart/000_020/1_2: Injected native terrain failure for 1_2\.; legacy fallback failed: Injected legacy terrain failure for 1_2\.$'
        }
    }
}

function Get-UnexpectedVisualGateErrorLines {
    param(
        [AllowEmptyString()]
        [string]$Text = "",

        [string[]]$AllowedErrorPatterns = @()
    )

    foreach ($line in ($Text -split "`r?`n")) {
        if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
                $line,
                '\bERROR\b',
                [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
            continue
        }

        $allowed = $false
        foreach ($pattern in $AllowedErrorPatterns) {
            if ([System.Text.RegularExpressions.Regex]::IsMatch(
                    $line,
                    $pattern,
                    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
                $allowed = $true
                break
            }
        }

        if (-not $allowed) {
            $line
        }
    }
}

function Get-VisualGateDiagnosticReasons {
    param(
        [AllowEmptyString()]
        [string]$Stdout = "",

        [AllowEmptyString()]
        [string]$Stderr = "",

        [string[]]$AllowedErrorPatterns = @(),

        [string[]]$RequiredStdoutPatterns = @()
    )

    $reasons = @()
    $leakPattern = '(?im)^.*(?:\bleaked\b|\bleaks?\s+detected\b).*$'
    if ([System.Text.RegularExpressions.Regex]::IsMatch($Stdout, $leakPattern)) {
        $reasons += "stdout reports leaked instances or resources"
    }
    if ([System.Text.RegularExpressions.Regex]::IsMatch($Stderr, $leakPattern)) {
        $reasons += "stderr reports leaked instances or resources"
    }

    if (@(Get-UnexpectedVisualGateErrorLines `
            -Text $Stdout `
            -AllowedErrorPatterns $AllowedErrorPatterns).Count -gt 0) {
        $reasons += "stdout reports unexpected ERROR lines"
    }
    if (@(Get-UnexpectedVisualGateErrorLines `
            -Text $Stderr `
            -AllowedErrorPatterns $AllowedErrorPatterns).Count -gt 0) {
        $reasons += "stderr reports unexpected ERROR lines"
    }

    $resultLines = @($Stdout -split "`r?`n" | Where-Object {
            [System.Text.RegularExpressions.Regex]::IsMatch(
                $_,
                '(?:^|\s)result=',
                [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        })
    $lastResult = if ($resultLines.Count -gt 0) { $resultLines[-1] } else { "" }
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
            $lastResult,
            '(?:^|\s)result=PASS(?:\s|$)',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
        $reasons += "stdout does not report result=PASS"
    }

    foreach ($pattern in $RequiredStdoutPatterns) {
        if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
                $Stdout,
                $pattern,
                [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
            $reasons += "stdout does not satisfy coverage requirement '$pattern'"
        }
    }

    $reasons
}
