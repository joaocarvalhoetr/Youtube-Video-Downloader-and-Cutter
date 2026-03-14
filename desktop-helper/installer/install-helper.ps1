$ErrorActionPreference = "Stop"

$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$installRoot = Join-Path $env:LOCALAPPDATA "YoutubeClipHelper"
$localDotnetRoot = Join-Path $installRoot "dotnet"
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValueName = "YoutubeClipHelper"
$helperExePath = Join-Path $installRoot "LocalClipHelper.exe"
$helperDllPath = Join-Path $installRoot "LocalClipHelper.dll"

function Stop-RunningHelper {
    $runningProcesses = Get-CimInstance Win32_Process | Where-Object {
        ($_.ExecutablePath -and $_.ExecutablePath -ieq $helperExePath) -or
        ($_.Name -ieq "dotnet.exe" -and $_.CommandLine -and $_.CommandLine -like "*$helperDllPath*")
    }

    foreach ($process in $runningProcesses) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 800
}

function Copy-WithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    $attempt = 0
    while ($true) {
        try {
            & $Action
            return
        } catch {
            $attempt++
            if ($attempt -ge 5) {
                throw
            }

            Start-Sleep -Milliseconds 600
        }
    }
}

Write-Host "Installing YoutubeClipHelper into $installRoot"

Stop-RunningHelper

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $installRoot "jobs") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $installRoot "output") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $installRoot "temp") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $installRoot "tools") | Out-Null

Copy-WithRetry {
    Get-ChildItem -Path $sourceRoot -Filter "LocalClipHelper*" | Copy-Item -Destination $installRoot -Force
}

if (Test-Path (Join-Path $sourceRoot "web.config")) {
    Copy-WithRetry {
        Copy-Item -Force (Join-Path $sourceRoot "web.config") (Join-Path $installRoot "web.config")
    }
}

Copy-WithRetry {
    Copy-Item -Force (Join-Path $sourceRoot "launch-helper.vbs") (Join-Path $installRoot "launch-helper.vbs")
}

Copy-WithRetry {
    Copy-Item -Force (Join-Path $sourceRoot "uninstall-helper.ps1") (Join-Path $installRoot "uninstall-helper.ps1")
}

if (-not (Test-Path (Join-Path $localDotnetRoot "dotnet.exe"))) {
    $dotnetInstallScript = Join-Path $env:TEMP "dotnet-install.ps1"
    Write-Host "Downloading local .NET runtime..."
    Invoke-WebRequest -UseBasicParsing "https://dot.net/v1/dotnet-install.ps1" -OutFile $dotnetInstallScript

    & powershell -NoProfile -ExecutionPolicy Bypass -File $dotnetInstallScript `
        -Runtime aspnetcore `
        -Version 10.0.0 `
        -Architecture x64 `
        -InstallDir $localDotnetRoot `
        -NoPath
}

$runCommand = "wscript.exe `"$installRoot\launch-helper.vbs`""
Set-ItemProperty -Path $runKeyPath -Name $runValueName -Value $runCommand -Force

Start-Process -FilePath "wscript.exe" -ArgumentList "`"$installRoot\launch-helper.vbs`"" -WindowStyle Hidden

Write-Host ""
Write-Host "YoutubeClipHelper installed successfully."
Write-Host "Output folder: $(Join-Path $installRoot 'output')"
