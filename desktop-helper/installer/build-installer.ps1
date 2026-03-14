$ErrorActionPreference = "Stop"

$installerRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Resolve-Path (Join-Path $installerRoot "..\LocalClipHelper")
$runtimeIdentifier = "win-x64"
$nugetSource = "https://api.nuget.org/v3/index.json"
$publishRoot = Join-Path $projectRoot "bin\Release\net10.0-windows\$runtimeIdentifier\publish"
$buildRoot = Join-Path $installerRoot "build"
$stagingRoot = Join-Path $buildRoot "staging"
$payloadZipPath = Join-Path $buildRoot "payload.zip"
$setupPath = Join-Path $buildRoot "YoutubeClipHelperSetup.exe"
$stubSourcePath = Join-Path $installerRoot "InstallerStub.cs"
$cscPath = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $cscPath)) {
    $cscPath = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
$projectFile = Join-Path $projectRoot "LocalClipHelper.csproj"
$appIconPath = Join-Path $projectRoot "Assets\app.ico"

Write-Host "Restoring LocalClipHelper runtime packs..."
dotnet restore $projectFile `
    -r $runtimeIdentifier `
    --source $nugetSource

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed."
}

Write-Host "Publishing LocalClipHelper..."
dotnet publish $projectFile `
    -c Release `
    -r $runtimeIdentifier `
    --no-restore `
    --self-contained true

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Remove-Item -Recurse -Force $stagingRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
Remove-Item -Force $payloadZipPath -ErrorAction SilentlyContinue
Remove-Item -Force $setupPath -ErrorAction SilentlyContinue

Copy-Item -Path (Join-Path $publishRoot "*") -Destination $stagingRoot -Recurse -Force
if (-not (Test-Path $cscPath)) {
    throw "Could not find the .NET Framework C# compiler at $cscPath"
}

Write-Host "Packing installer payload..."
Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $payloadZipPath -CompressionLevel Optimal

Write-Host "Compiling installer executable..."
& $cscPath `
    /nologo `
    /target:winexe `
    /out:$setupPath `
    /win32icon:$appIconPath `
    /resource:$payloadZipPath,PayloadZip `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /reference:System.Management.dll `
    /reference:System.Windows.Forms.dll `
    $stubSourcePath

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $setupPath)) {
    throw "Installer compilation failed."
}

Write-Host "Installer ready at $setupPath"
