Set-StrictMode -Version Latest

function Get-VisualGateAllowedErrorPatterns {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Scene
    )

    switch ($Scene) {
        "origin_applied_manifest_probe" {
            '^ERROR: ZoneLoader: Native terrain manifest is incompatible: res://content/league-slice/maps/inst-league-start/terrain-manifest\.json$'
        }
        "partial_native_terrain_fallback_probe" {
            '^ERROR: ZoneLoader: Native terrain scene is missing: res://content/league-slice/maps/inst-league-start/1_2/missing_terrain\.scn$'
        }
        "unrecoverable_terrain_failure_probe" {
            '^ERROR: ZoneLoader: Native terrain scene is listed more than once: res://content/league-slice/maps/inst-league-start/0_2/0_2_terrain\.scn$'
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
