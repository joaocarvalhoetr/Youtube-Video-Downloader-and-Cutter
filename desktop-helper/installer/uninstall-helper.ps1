$ErrorActionPreference = "Stop"

$installRoot = Join-Path $env:LOCALAPPDATA "YoutubeClipHelper"
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValueName = "YoutubeClipHelper"

Get-Process -Name "LocalClipHelper" -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $runKeyPath) {
    Remove-ItemProperty -Path $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue
}

if (Test-Path $installRoot) {
    Remove-Item -Recurse -Force $installRoot
}

Write-Host "YoutubeClipHelper removed."
