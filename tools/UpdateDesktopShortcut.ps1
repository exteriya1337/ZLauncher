# Creates or refreshes the ZLauncher shortcut on the current user's Desktop.
param(
    [Parameter(Mandatory = $true)]
    [string] $TargetPath,

    [Parameter(Mandatory = $false)]
    [string] $WorkingDirectory = "",

    [Parameter(Mandatory = $false)]
    [string] $IconPath = ""
)

$ErrorActionPreference = "Stop"

$TargetPath = $TargetPath.Trim().TrimEnd('\', '/')
$WorkingDirectory = $WorkingDirectory.Trim().TrimEnd('\', '/')
$IconPath = $IconPath.Trim().TrimEnd('\', '/')

# Prefer .exe for WinExe projects (MSBuild TargetPath can be the .dll)
if ($TargetPath.EndsWith(".dll", [StringComparison]::OrdinalIgnoreCase)) {
    $exeCandidate = [System.IO.Path]::ChangeExtension($TargetPath, ".exe")
    if (Test-Path -LiteralPath $exeCandidate) {
        $TargetPath = $exeCandidate
    }
}

if (-not (Test-Path -LiteralPath $TargetPath)) {
    Write-Warning "Target not found, shortcut not updated: $TargetPath"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
    $WorkingDirectory = Split-Path -Parent $TargetPath
}

$desktop = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktop "ZLauncher.lnk"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = (Resolve-Path -LiteralPath $TargetPath).Path
$shortcut.WorkingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory).Path
$shortcut.Description = "ZLauncher"
$shortcut.WindowStyle = 1

if (-not [string]::IsNullOrWhiteSpace($IconPath) -and (Test-Path -LiteralPath $IconPath)) {
    $shortcut.IconLocation = (Resolve-Path -LiteralPath $IconPath).Path + ",0"
}
else {
    $shortcut.IconLocation = $shortcut.TargetPath + ",0"
}

$shortcut.Save()
Write-Host "Desktop shortcut updated: $shortcutPath -> $($shortcut.TargetPath)"
