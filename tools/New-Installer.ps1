<#
.SYNOPSIS
    Builds the DFSR Analyser MSI (and a matching portable zip).

.DESCRIPTION
    Publishes DfsrAnalyser.exe and dfsra.exe self-contained for win-x64 into one folder, adds the
    README, and packages that folder twice:

      artifacts\DfsrAnalyser-<version>.msi    per-user installer: no administrator rights, no .NET
                                              install needed, Start menu shortcut, dfsra on the PATH,
                                              listed in Installed apps
      artifacts\DfsrAnalyser-<version>.zip    the same files to extract and run anywhere

    The version comes from Directory.Build.props unless given.

    Requires the WiX tool:  dotnet tool install --global wix   (version 5)

.PARAMETER Version
    Version to stamp on the binaries and the MSI, e.g. 1.0.0. Defaults to <Version> in Directory.Build.props.

.EXAMPLE
    .\tools\New-Installer.ps1
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish\DfsrAnalyser'

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    $Version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if (-not $Version) { throw 'No <Version> in Directory.Build.props; pass -Version.' }
}

# --- wix -----------------------------------------------------------------------------------------
$wix = Get-Command wix -ErrorAction SilentlyContinue
if ($wix) { $wix = $wix.Source }
else {
    $wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
    if (-not (Test-Path $wix)) { throw 'WiX was not found. Install it with:  dotnet tool install --global wix' }
}

# --- publish -------------------------------------------------------------------------------------
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force $publish | Out-Null

foreach ($project in 'src\DfsrAnalyser.App\DfsrAnalyser.App.csproj', 'src\DfsrAnalyser.Cli\DfsrAnalyser.Cli.csproj') {
    Write-Host "Publishing $project ($Version) ..." -ForegroundColor Cyan
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -o $publish `
        -p:Version=$Version -p:DebugType=none -p:GenerateDocumentationFile=false -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}
Copy-Item (Join-Path $root 'README.md') $publish

foreach ($exe in 'DfsrAnalyser.exe', 'dfsra.exe') {
    if (-not (Test-Path (Join-Path $publish $exe))) { throw "$exe is missing from the publish output." }
}

# --- msi -----------------------------------------------------------------------------------------
$msi = Join-Path $artifacts "DfsrAnalyser-$Version.msi"
Write-Host "Building $msi ..." -ForegroundColor Cyan
& $wix build (Join-Path $root 'installer\DfsrAnalyser.wxs') -arch x64 `
    -define "Version=$Version" `
    -define "PublishDir=$publish" `
    -define "IconFile=$(Join-Path $root 'src\DfsrAnalyser.App\DfsrAnalyser.ico')" `
    -out $msi
if ($LASTEXITCODE -ne 0) { throw 'WiX build failed.' }
Remove-Item (Join-Path $artifacts "DfsrAnalyser-$Version.wixpdb") -ErrorAction SilentlyContinue

# --- zip -----------------------------------------------------------------------------------------
$zip = Join-Path $artifacts "DfsrAnalyser-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $publish -DestinationPath $zip

$files = (Get-ChildItem $publish -Recurse -File).Count
Write-Host ''
Write-Host ("MSI: {0} ({1:N0} MB)" -f $msi, ((Get-Item $msi).Length / 1MB)) -ForegroundColor Green
Write-Host ("Zip: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
Write-Host "     $files files, version $Version" -ForegroundColor DarkGray
