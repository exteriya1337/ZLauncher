# Publish online Setup stub (always downloads latest Portable from GitHub).
# Does NOT embed payload — file stays relatively small.
param(
    [string]$OutDir = "",
    [switch]$WithOfflinePayload
)

$ErrorActionPreference = "Stop"
$installerRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $installerRoot

if (-not $OutDir) {
    $OutDir = Join-Path $repoRoot "publish\setup-online"
}

if ($WithOfflinePayload) {
    Write-Host "=== Pack offline payload (optional) ==="
    & (Join-Path $PSScriptRoot "Pack-Payload.ps1")
} else {
    # Ensure no huge embedded zip in online stub
    $payloadZip = Join-Path $installerRoot "payload\payload.zip"
    if (Test-Path $payloadZip) {
        Write-Host "Removing payload.zip so Setup stays online-only stub..."
        Remove-Item $payloadZip -Force
    }
}

Write-Host "=== Publish single-file online Setup ==="
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

$pub = Join-Path $env:TEMP ("zlauncher_setup_online_" + [guid]::NewGuid().ToString("N"))
dotnet publish (Join-Path $installerRoot "ZLauncher.Installer.csproj") `
    -c Release -r win-x64 --self-contained true -o $pub `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    /p:IncludeAllContentForSelfExtract=true `
    /p:DebugType=None /p:DebugSymbols=false --nologo

$exeName = "ZLauncher.Setup.exe"
$srcExe = Join-Path $pub $exeName
if (-not (Test-Path $srcExe)) { Write-Error "Publish failed: $srcExe" }

Copy-Item $srcExe (Join-Path $OutDir $exeName) -Force
Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue

$mb = [math]::Round((Get-Item (Join-Path $OutDir $exeName)).Length / 1MB, 1)
Write-Host ""
Write-Host "Online Setup: $OutDir\$exeName  ($mb MB)"
Write-Host "This stub downloads ZLauncher-Portable.zip from GitHub releases/latest"
