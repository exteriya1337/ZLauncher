# Publish-Setup.ps1 — build portable (optional) + pack + single-file Setup
param(
    [string]$OutDir = "",
    [string]$PortableDir = "",
    [switch]$SkipPublishLauncher
)

$ErrorActionPreference = "Stop"
$installerRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $installerRoot

if (-not $OutDir) {
    $OutDir = Join-Path ([Environment]::GetFolderPath("Desktop")) "ZLauncher-Setup"
}

if (-not $SkipPublishLauncher -and -not $PortableDir) {
    Write-Host "=== 0) Publish portable launcher ==="
    $PortableDir = Join-Path $repoRoot "publish\portable"
    if (Test-Path $PortableDir) { Remove-Item $PortableDir -Recurse -Force }
    New-Item -ItemType Directory -Path $PortableDir | Out-Null
    dotnet publish (Join-Path $repoRoot "ZLauncher.csproj") `
        -c Release -r win-x64 --self-contained true -o $PortableDir `
        /p:DebugType=None /p:DebugSymbols=false --nologo
}

Write-Host "=== 1) Pack payload.zip ==="
& (Join-Path $PSScriptRoot "Pack-Payload.ps1") -PortableDir $PortableDir

Write-Host "=== 2) Publish single-file Setup ==="
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

$pub = Join-Path $env:TEMP ("zlauncher_setup_pub_" + [guid]::NewGuid().ToString("N"))
dotnet publish (Join-Path $installerRoot "ZLauncher.Installer.csproj") `
    -c Release -r win-x64 --self-contained true -o $pub `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    /p:DebugType=None /p:DebugSymbols=false --nologo

$exeName = "ZLauncher.Setup.exe"
$srcExe = Join-Path $pub $exeName
if (-not (Test-Path $srcExe)) { Write-Error "Publish failed: $srcExe" }

Copy-Item $srcExe (Join-Path $OutDir $exeName) -Force
Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue

@"
ZLauncher Setup
===============
Run: ZLauncher.Setup.exe

Source: https://github.com/exteriya1337/ZLauncher
Default path: %LocalAppData%\Programs\ZLauncher
"@ | Set-Content (Join-Path $OutDir "README.txt") -Encoding UTF8

$mb = [math]::Round((Get-Item (Join-Path $OutDir $exeName)).Length / 1MB, 1)
Write-Host ""
Write-Host "Done:  $OutDir\$exeName  ($mb MB)"
