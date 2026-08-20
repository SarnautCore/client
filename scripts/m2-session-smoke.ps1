<#
.SYNOPSIS
    Drives the client's session shell against a live auth service and shard:
    register or log in, create a character, select it, and enter InstLeague1 at
    the character's own spawn.

.DESCRIPTION
    The evidence for the M2 session shell that CI cannot produce. It needs a
    shard, an auth service, and the infrastructure both refuse to start without,
    so it is a manual script rather than a workflow step.

    What it proves, in order:

      1. The account HTTP API works from the client's own `AuthClient`.
      2. The character-creation option list, the spawn and the starting kit come
         from the server, not from client constants (ADR 0032 §2): the spawn the
         shard answers with is compared against `GET /v1/chargen/options`.
      3. The opaque ticket flows into `EnterZoneRequest` (ADR 0030 §2).
      4. Killing the client and signing in again finds the existing character and
         re-enters at its saved position, not at the chargen spawn.
      5. Nothing printed carries a password or a token.

    The Godot screens bind to the same view models this exercises; what is not
    covered here is the binding itself, which is why the screens hold no rules.

    Start the infrastructure first:

        docker compose -f ..\infra\compose\docker-compose.yml up -d

.PARAMETER ServerRepo
    Path to a checkout of SarnautCore/server, which supplies auth, the shard and
    the vendored fixture pack.
#>
param(
    [string]$ServerRepo = "../server",
    [string]$Address = "127.0.0.1:4452",
    [string]$AuthAddress = "127.0.0.1:8593",
    [string]$ShardHealthAddress = "127.0.0.1:8591",
    [string]$AuthHealthAddress = "127.0.0.1:8592",
    [string]$PostgresDsn = "postgres://sarnaut:sarnaut_dev@127.0.0.1:5433/sarnaut?sslmode=disable",
    [string]$ValkeyAddress = "127.0.0.1:6379",
    [string]$NatsUrl = "nats://127.0.0.1:4222"
)

$ErrorActionPreference = "Stop"
$clientRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$serverRoot = (Resolve-Path (Join-Path $clientRoot $ServerRepo)).Path
$contentPack = Join-Path $serverRoot "testdata\packs\demo"
$zoneId = "InstLeague1"
$packId = (Get-Content -LiteralPath (Join-Path $contentPack "manifest.json") -Raw | ConvertFrom-Json).pack_id

function Resolve-Go {
    $command = Get-Command go -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    $windowsGo = "C:\Program Files\Go\bin\go.exe"
    if ($IsWindows -and (Test-Path -LiteralPath $windowsGo -PathType Leaf)) { return $windowsGo }
    throw "Go was not found on PATH. Install the version from the server's go.mod."
}

function Wait-Ready {
    param([string]$Uri, [System.Diagnostics.Process]$Process, [string]$Name, [string]$ErrorLog)

    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        if ($Process.HasExited) {
            $detail = Get-Content -LiteralPath $ErrorLog -Raw -ErrorAction SilentlyContinue
            throw "$Name exited before becoming ready. $detail"
        }
        try {
            if ((Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 1).StatusCode -eq 200) { return }
        }
        catch {
        }
        Start-Sleep -Milliseconds 250
    }
    $detail = Get-Content -LiteralPath $ErrorLog -Raw -ErrorAction SilentlyContinue
    throw "$Name did not become ready at $Uri within 20 seconds. $detail"
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("sarnaut-session-" + [Guid]::NewGuid().ToString("N"))
$binaryExtension = if ($IsWindows) { ".exe" } else { "" }
$processes = @()

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $contentPack "manifest.json") -PathType Leaf)) {
        throw "The server's fixture pack was not found at $contentPack."
    }

    Write-Output "== building the server side =="
    $goExecutable = Resolve-Go
    Push-Location $serverRoot
    try {
        foreach ($component in @("auth", "shard", "migrate")) {
            & $goExecutable build -o (Join-Path $temporaryRoot ($component + $binaryExtension)) "./cmd/$component"
            if ($LASTEXITCODE -ne 0) { throw "Building cmd/$component failed with exit code $LASTEXITCODE." }
        }
        $env:SARNAUT_POSTGRES_DSN = $PostgresDsn
        & (Join-Path $temporaryRoot ("migrate" + $binaryExtension)) up
        if ($LASTEXITCODE -ne 0) { throw "Migrations failed. Is infra/compose running?" }
    }
    finally {
        Pop-Location
    }

    Write-Output "== building the client smoke =="
    & dotnet build (Join-Path $clientRoot "tools\SarnautCore.NetSmoke\SarnautCore.NetSmoke.csproj") --configuration Debug
    if ($LASTEXITCODE -ne 0) { throw "Building the client smoke failed." }
    $smoke = Join-Path $clientRoot "tools\SarnautCore.NetSmoke\bin\Debug\net10.0\SarnautCore.NetSmoke.dll"

    foreach ($service in @(
            @{ Name = "auth"; Environment = @{
                    SARNAUT_HEALTH_ADDRESS      = $AuthHealthAddress
                    SARNAUT_AUTH_LISTEN_ADDRESS = $AuthAddress
                    SARNAUT_CONTENT_PACK        = $contentPack
                    SARNAUT_POSTGRES_DSN        = $PostgresDsn
                    SARNAUT_VALKEY_ADDRESS      = $ValkeyAddress
                    SARNAUT_NATS_URL            = $NatsUrl
                    SARNAUT_OTEL_ENDPOINT       = ""
                }; Ready = "http://$AuthHealthAddress/readyz"
            },
            @{ Name = "shard"; Environment = @{
                    SARNAUT_QUIC_LISTEN_ADDRESS = $Address
                    SARNAUT_HEALTH_ADDRESS      = $ShardHealthAddress
                    SARNAUT_CONTENT_PACK        = $contentPack
                    SARNAUT_WORLD_ZONE_ID       = $zoneId
                    SARNAUT_POSTGRES_DSN        = $PostgresDsn
                    SARNAUT_VALKEY_ADDRESS      = $ValkeyAddress
                    SARNAUT_NATS_URL            = $NatsUrl
                    SARNAUT_OTEL_ENDPOINT       = ""
                }; Ready = "http://$ShardHealthAddress/readyz"
            })) {
        Write-Output ("== starting {0} ==" -f $service.Name)
        foreach ($name in $service.Environment.Keys) {
            [System.Environment]::SetEnvironmentVariable($name, $service.Environment[$name], "Process")
        }
        $stdout = Join-Path $temporaryRoot ($service.Name + ".stdout.log")
        $stderr = Join-Path $temporaryRoot ($service.Name + ".stderr.log")
        $process = Start-Process -FilePath (Join-Path $temporaryRoot ($service.Name + $binaryExtension)) `
            -WorkingDirectory $serverRoot -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        $processes += $process
        Wait-Ready -Uri $service.Ready -Process $process -Name $service.Name -ErrorLog $stderr
    }

    # An account per run, so a rerun does not depend on what the last one left
    # behind. A character name is 3 to 16 ASCII letters (ADR 0032 §3).
    $runId = -join ((1..6) | ForEach-Object { [char](97 + (Get-Random -Maximum 26)) })
    $email = "shell-$runId@example.invalid"
    $password = "shell-password-$runId"
    $characterName = "Shell" + $runId

    Write-Output "== register, create a character, enter the world =="
    $firstRun = & dotnet $smoke --address $Address --zone $zoneId --pack $packId `
        --auth "http://$AuthAddress" --email $email --password $password --character $characterName --duration 12
    if ($LASTEXITCODE -ne 0) { throw "The first run failed with exit code $LASTEXITCODE." }
    $firstRun | ForEach-Object { Write-Output $_ }

    $first = ($firstRun | Select-String -Pattern "spawn=([-0-9.e+]+),([-0-9.e+]+),([-0-9.e+]+) chargen_spawn=([-0-9.e+]+),([-0-9.e+]+),([-0-9.e+]+)").Matches[0].Groups
    $firstX = [double]$first[1].Value
    $firstY = [double]$first[2].Value
    if ([math]::Abs($firstX - [double]$first[4].Value) -gt 0.001 -or
        [math]::Abs($firstY - [double]$first[5].Value) -gt 0.001) {
        throw "A fresh character entered at $firstX,$firstY rather than at the chargen spawn."
    }
    Write-Output "PASS entered at the chargen spawn $firstX,$firstY"

    Write-Output "== signing in again finds the existing character and its saved position =="
    $secondRun = & dotnet $smoke --address $Address --zone $zoneId --pack $packId `
        --auth "http://$AuthAddress" --email $email --password $password --character $characterName --duration 12
    if ($LASTEXITCODE -ne 0) { throw "The reconnect run failed with exit code $LASTEXITCODE." }
    $secondRun | ForEach-Object { Write-Output $_ }

    $secondX = [double]($secondRun | Select-String -Pattern "spawn=([-0-9.e+]+),").Matches[0].Groups[1].Value
    if ($secondX -le $firstX) {
        throw "The reconnect entered at x=$secondX, not east of the first spawn x=${firstX}: the position was not restored."
    }
    Write-Output "PASS reconnected at the saved position $secondX"

    foreach ($output in @($firstRun, $secondRun)) {
        foreach ($secretValue in @($password, $email)) {
            if (($output -join "`n") -match [regex]::Escape($secretValue)) {
                throw "The client smoke printed a secret."
            }
        }
        if (($output -join "`n") -match "sarnaut_tk_" -or ($output -join "`n") -match "sarnaut_as_") {
            throw "The client smoke printed a token."
        }
    }
    Write-Output "PASS no password, email or token appears in the client output"

    Write-Output "m2-session-smoke: OK"
}
finally {
    foreach ($process in $processes) {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolved = [System.IO.Path]::GetFullPath($temporaryRoot)
        $safeParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if ($resolved.StartsWith($safeParent, [StringComparison]::OrdinalIgnoreCase) -and $resolved -ne $safeParent) {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
