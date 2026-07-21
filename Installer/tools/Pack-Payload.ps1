# Pack-Payload.ps1 — zip portable launcher into Installer/payload/payload.zip
param(
    [string]$PortableDir = ""
)

$ErrorActionPreference = "Stop"
$installerRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $installerRoot

if (-not $PortableDir) {
    $candidates = @(
        (Join-Path $repoRoot "publish\portable"),
        (Join-Path ([Environment]::GetFolderPath("Desktop")) "ZLauncher-Portable"),
        (Join-Path $repoRoot "bin\Release\net10.0")
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "ZLauncher.exe")) {
            $PortableDir = $c
            break
        }
    }
}

if (-not $PortableDir -or -not (Test-Path (Join-Path $PortableDir "ZLauncher.exe"))) {
    Write-Error "ZLauncher.exe not found. Publish portable first or pass -PortableDir."
}

$payloadDir = Join-Path $installerRoot "payload"
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
$zip = Join-Path $payloadDir "payload.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "Packing: $PortableDir -> $zip"
Compress-Archive -Path (Join-Path $PortableDir "*") -DestinationPath $zip -Force
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host "OK ($mb MB)"
