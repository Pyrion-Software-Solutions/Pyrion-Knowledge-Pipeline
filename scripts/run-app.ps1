<#
.SYNOPSIS
Frees port 5187, starts Pyrion, and opens the browser once the app is ready.
.EXAMPLE
.\scripts\run-app.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src\PyrionKnowledgePipeline.Web\PyrionKnowledgePipeline.Web.csproj'
$appPort = 5187
$appUrl = 'http://localhost:5187'
$launcherExitCode = 0
$locationPushed = $false
$browserJob = $null

function Get-AppPortOwnerIds {
    # Include bound/established connections as well as listeners. Ignore expired
    # connection records whose process has already exited (including PID 0).
    Get-NetTCPConnection -ErrorAction Stop |
        Where-Object { $_.LocalPort -eq $appPort -and $_.OwningProcess -gt 0 } |
        Select-Object -ExpandProperty OwningProcess -Unique |
        Where-Object { $null -ne (Get-Process -Id $_ -ErrorAction SilentlyContinue) }
}

try {
    # Resolve global.json and the project from the script, not the caller's folder.
    Push-Location -LiteralPath $projectRoot
    $locationPushed = $true

    $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw 'The .NET 10 SDK is required. Install it, then run this script again.'
    }

    # Verify the SDK before stopping an existing application.
    & $dotnetCommand.Source --version
    if ($LASTEXITCODE -ne 0) {
        throw 'The .NET SDK required by global.json is unavailable. Install the required SDK and try again.'
    }

    # Query all addresses so both IPv4 and IPv6 owners are stopped.
    $ownerIds = @(Get-AppPortOwnerIds)
    foreach ($ownerId in $ownerIds) {
        if ($ownerId -le 4 -or $ownerId -eq $PID) {
            throw "Port $appPort belongs to a system process or this launcher and cannot be stopped safely."
        }

        $portOwner = Get-Process -Id $ownerId -ErrorAction SilentlyContinue
        if ($null -eq $portOwner) {
            continue
        }

        Write-Host "Stopping $($portOwner.ProcessName) (PID $ownerId) to free port $appPort..."
        try {
            Stop-Process -InputObject $portOwner -Force -ErrorAction Stop
            if (-not $portOwner.WaitForExit(10000)) {
                throw 'The process did not exit within 10 seconds.'
            }
        }
        catch {
            throw "Could not stop PID $ownerId on port ${appPort}: $($_.Exception.Message) Close that process and run this script again."
        }
    }

    # Do not build or start the replacement until the listening port is released.
    $releaseDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $remainingOwners = @(Get-AppPortOwnerIds)
        if ($remainingOwners.Count -eq 0) {
            break
        }
        if ([DateTime]::UtcNow -ge $releaseDeadline) {
            throw "Port $appPort is still occupied. Close the process using it and run this script again."
        }
        Start-Sleep -Milliseconds 250
    }
    while ($true)

    Write-Host "Port $appPort is free."
    Write-Host ''
    Write-Host 'Starting Pyrion Knowledge Pipeline...'
    Write-Host "Your default browser will open $appUrl when the app is ready."
    Write-Host 'Keep this window open. Press Ctrl+C to stop the app.'
    Write-Host 'LM Studio must be running to use AI analysis.'
    Write-Host ''

    # Check readiness concurrently while dotnet runs in the foreground, keeping
    # startup output and Ctrl+C available in this window.
    $browserJob = Start-Job -ArgumentList $appUrl -ScriptBlock {
        param($url)
        $ErrorActionPreference = 'Stop'
        $readyDeadline = [DateTime]::UtcNow.AddMinutes(3)
        while ([DateTime]::UtcNow -lt $readyDeadline) {
            $isReady = $false
            try {
                $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2
                $isReady = $response.StatusCode -eq 200 -and
                    $response.Content.Contains('PyrionKnowledgePipeline.Web')
            }
            catch {
                # The server may still be restoring packages or building.
            }

            if ($isReady) {
                Start-Process -FilePath $url -ErrorAction Stop
                Write-Output "Pyrion is ready. Opened $url in your default browser."
                return
            }
            Start-Sleep -Milliseconds 500
        }
        Write-Warning "The app did not become ready within three minutes. Check the server output, then open $url manually."
    }

    # dotnet run restores packages and builds the app when necessary.
    & $dotnetCommand.Source run --project $projectFile --launch-profile http -- --urls $appUrl
    $launcherExitCode = $LASTEXITCODE
}
catch {
    Write-Host "The app could not start: $($_.Exception.Message)" -ForegroundColor Red
    $launcherExitCode = 1
}
finally {
    # Never leave a readiness check that could open a browser after the app exits.
    if ($null -ne $browserJob) {
        Stop-Job -Job $browserJob -ErrorAction SilentlyContinue
        Receive-Job -Job $browserJob -ErrorAction Continue
        Remove-Job -Job $browserJob -Force -ErrorAction SilentlyContinue
    }
    if ($locationPushed) {
        Pop-Location
    }
}

exit $launcherExitCode
