<#
.SYNOPSIS
    Removes DFSR Analyser for the current user: the install folder, the Start menu shortcut, the PATH entry and
    the Settings > Apps listing. Only what Install-DfsrAnalyser.ps1 added.

.DESCRIPTION
    Your cases are never touched. The app's settings (%LOCALAPPDATA%\DfsrAnalyser: recent cases, UTC/local) are
    kept unless -RemoveSettings is given, so a reinstall picks up where you left off.

.PARAMETER InstallPath     Default: the folder this script is in when it's in an install, else the default install folder.
.PARAMETER RemoveSettings  Also delete the app's settings.
.PARAMETER FromArp         Set when Settings > Apps runs it: keeps the window open at the end.
#>
# Adapted from ADTD for PowerPoint's tools/Uninstall-Adtd.ps1.
[CmdletBinding()]
param(
    [string] $InstallPath,
    [switch] $RemoveSettings,
    [switch] $FromArp
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appName = 'DFSR Analyser'
$exeName = 'DfsrAnalyser.exe'
if (-not $InstallPath) {
    $InstallPath = if (Test-Path (Join-Path $PSScriptRoot $exeName)) { $PSScriptRoot } else { Join-Path $env:LOCALAPPDATA "Programs\$appName" }
}
$InstallPath = [System.IO.Path]::GetFullPath($InstallPath)
$exe = Join-Path $InstallPath $exeName

Write-Host ''
Write-Host "Removing $appName" -ForegroundColor Cyan
Write-Host ''
try {
    $running = Get-Process -Name 'DfsrAnalyser', 'dfsra' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallPath, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw "$appName is running. Close it (and any dfsra command) and run the uninstaller again." }

    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
    if (Test-Path $lnk) { Remove-Item -Force $lnk; Write-Host '  removed                       Start menu shortcut' }

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath) {
        $parts = @($userPath -split ';' | Where-Object { $_ })
        if ($parts -contains $InstallPath) {
            [Environment]::SetEnvironmentVariable('Path', (($parts | Where-Object { $_ -ne $InstallPath }) -join ';'), 'User')
            Write-Host '  removed                       PATH entry'
        }
    }

    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DfsrAnalyser'
    if (Test-Path $uninstallKey) { Remove-Item -Recurse -Force $uninstallKey; Write-Host '  removed                       Settings > Apps listing' }

    # Only delete a folder that is recognisably an install of this app.
    if (Test-Path $exe) {
        Set-Location $env:TEMP   # this script may be running from inside the folder
        Remove-Item -Recurse -Force $InstallPath
        Write-Host "  removed                       $InstallPath"
    }
    elseif (Test-Path $InstallPath) { Write-Host "  left alone                    $InstallPath (no $exeName in it)" -ForegroundColor Yellow }

    $data = Join-Path $env:LOCALAPPDATA 'DfsrAnalyser'
    if (Test-Path $data) {
        if ($RemoveSettings) { Remove-Item -Recurse -Force $data; Write-Host "  removed                       $data (settings)" }
        else { Write-Host "  kept                          $data (settings; -RemoveSettings deletes them)" }
    }

    Write-Host ''
    Write-Host "$appName has been removed. Your cases have not been touched." -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
}
Write-Host ''
if ($FromArp) { Read-Host 'Press Enter to close' | Out-Null }
