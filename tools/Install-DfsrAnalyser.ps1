<#
.SYNOPSIS
    Installs DFSR Analyser for the current user from an extracted release zip. No administrator rights needed.

.DESCRIPTION
    - Removes a copy installed by the old 1.0.0 MSI, if there is one.
    - Copies the app and the dfsra command line to the install folder, replacing an earlier version.
    - Adds a Start menu shortcut and puts the install folder on your PATH, so dfsra works in any new terminal.
    - Lists the app in Settings > Apps, where it can be uninstalled.

    Everything is per-user: files under %LOCALAPPDATA%, settings under HKEY_CURRENT_USER. The app's own settings
    (%LOCALAPPDATA%\DfsrAnalyser) and your cases are left as they are, so an upgrade keeps them.

    Runs either as a file (from "Install DFSR Analyser.cmd" or PowerShell) or as a script block handed over by
    install.ps1, the one-line installer. In the second case it must not exit, which would close the user's
    PowerShell window, so failures are thrown instead, marked as already reported.

.PARAMETER Source       The extracted release folder (holding app\). Default: the folder this script is in.
.PARAMETER InstallPath  Where to install. Default: %LOCALAPPDATA%\Programs\DFSR Analyser.
.PARAMETER NoPath       Don't add the install folder to your PATH.
.PARAMETER Version      Shown in Settings > Apps. Default: read from the app.
#>
# Adapted from ADTD for PowerPoint's tools/Install-Adtd.ps1 and AlignPro's tools/Install-AlignPro.ps1.
[CmdletBinding()]
param(
    [string] $Source,
    [string] $InstallPath = (Join-Path $env:LOCALAPPDATA 'Programs\DFSR Analyser'),
    [switch] $NoPath,
    [string] $Version
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runAsFile = $MyInvocation.MyCommand.CommandType -eq 'ExternalScript'

function Fail {
    param([string] $Message, [string] $Remedy)
    Write-Host ''
    Write-Host $Message -ForegroundColor Red
    if ($Remedy) { Write-Host $Remedy -ForegroundColor Yellow }
    Write-Host ''
    if ($runAsFile) { exit 1 }
    $reported = [System.Exception]::new($Message)
    $reported.Data['DfsrAnalyser.Reported'] = $true
    throw $reported
}

$appName = 'DFSR Analyser'
$exeName = 'DfsrAnalyser.exe'
$keyName = 'DfsrAnalyser'
# Not a parameter default: Windows PowerShell 5.1 leaves $PSScriptRoot empty there in an advanced script.
# Run as a script block (install.ps1), $PSScriptRoot is empty anyway and -Source is always passed.
if (-not $Source) { $Source = $PSScriptRoot }
if (-not $Source) { Fail 'No -Source folder given.' 'Run this from the extracted release zip.' }
$app = Join-Path $Source 'app'
if (-not (Test-Path (Join-Path $app $exeName))) {
    Fail "No app folder with $exeName in it next to this script ($app)." 'Run this from the extracted release zip.'
}
if (-not $Version) { $Version = (Get-Item (Join-Path $app $exeName)).VersionInfo.ProductVersion -replace '\+.*$', '' }
$InstallPath = [System.IO.Path]::GetFullPath($InstallPath)

Write-Host ''
Write-Host "$appName $Version" -ForegroundColor Cyan
Write-Host ''

$running = Get-Process -Name 'DfsrAnalyser', 'dfsra' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallPath, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { Fail "$appName is running from the install folder." 'Close it (and any dfsra command) and run the installer again.' }

# --- an earlier MSI install --------------------------------------------------------------------------
# Version 1.0.0 was also built as a per-user MSI. Left in place it would keep its own Settings > Apps
# entry and, on a later repair or uninstall, delete files this installer put in the same folder.
$msiKeys = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -match '^\{[0-9A-Fa-f-]{36}\}$' } |
    Where-Object { $p = Get-ItemProperty $_.PSPath; ($p.PSObject.Properties['DisplayName'] -and $p.DisplayName -eq $appName) })
foreach ($key in $msiKeys) {
    $code = $key.PSChildName
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList '/x', $code, '/qn', '/norestart' -Wait -PassThru
    if ($process.ExitCode -notin 0, 1605, 3010) {
        Fail "The earlier MSI install of $appName could not be removed (msiexec exit code $($process.ExitCode))." `
             'Uninstall it from Settings > Apps > Installed apps, then run this installer again.'
    }
    Write-Host "  removed                       the earlier MSI install ($code)"
}

# --- files -------------------------------------------------------------------------------------------
try {
    if (Test-Path $InstallPath) { Get-ChildItem -LiteralPath $InstallPath -Force | Remove-Item -Recurse -Force }
    else { New-Item -ItemType Directory -Force $InstallPath | Out-Null }
    Copy-Item -Path (Join-Path $app '*') -Destination $InstallPath -Recurse -Force
    foreach ($file in 'Uninstall-DfsrAnalyser.ps1', 'LICENSE', 'NOTICE.md') {
        $from = Join-Path $Source $file
        if (Test-Path $from) { Copy-Item -LiteralPath $from -Destination $InstallPath -Force }
    }
}
catch { Fail "Could not copy the files to ${InstallPath}: $($_.Exception.Message)" 'Close anything using that folder and try again.' }
$exe = Join-Path $InstallPath $exeName
Write-Host "  installed to                  $InstallPath"

# --- Start menu --------------------------------------------------------------------------------------
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($lnk)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $InstallPath
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'Import or collect DFSR debug logs and chart event rates over time'
$shortcut.Save()
Write-Host "  Start menu                    $appName"

# --- PATH --------------------------------------------------------------------------------------------
if (-not $NoPath) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @(if ($userPath) { $userPath -split ';' | Where-Object { $_ } })
    if ($parts -notcontains $InstallPath) {
        [Environment]::SetEnvironmentVariable('Path', (@($parts) + $InstallPath) -join ';', 'User')
    }
    Write-Host "  PATH                          dfsra (open a new terminal to use it)"
}

# --- Settings > Apps ---------------------------------------------------------------------------------
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$keyName"
New-Item -Path $uninstallKey -Force | Out-Null
$bytes = (Get-ChildItem -LiteralPath $InstallPath -File -Recurse | Measure-Object -Property Length -Sum).Sum
Set-ItemProperty -Path $uninstallKey -Name 'DisplayName' -Value $appName
Set-ItemProperty -Path $uninstallKey -Name 'DisplayVersion' -Value $Version
Set-ItemProperty -Path $uninstallKey -Name 'Publisher' -Value 'Mark Renoden'
Set-ItemProperty -Path $uninstallKey -Name 'InstallLocation' -Value $InstallPath
Set-ItemProperty -Path $uninstallKey -Name 'DisplayIcon' -Value "$exe,0"
Set-ItemProperty -Path $uninstallKey -Name 'URLInfoAbout' -Value 'https://github.com/MrR1974/DFSR-Analyser'
Set-ItemProperty -Path $uninstallKey -Name 'EstimatedSize' -Value ([int]($bytes / 1KB)) -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'NoRepair' -Value 1 -Type DWord
# -FromArp: launched by Settings, so the uninstaller keeps its window open to show what it did.
Set-ItemProperty -Path $uninstallKey -Name 'UninstallString' -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}" -FromArp' -f (Join-Path $InstallPath 'Uninstall-DfsrAnalyser.ps1'))
Write-Host "  listed in                     Settings > Apps > Installed apps"

Write-Host ''
Write-Host "Done. Start $appName from the Start menu; Help > User guide (F1) explains how to use it." -ForegroundColor Green
Write-Host ''
