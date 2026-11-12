<#
.SYNOPSIS
    Builds the release zip: DfsrAnalyser.exe and the dfsra command line, self-contained (no .NET install
    needed), with the installer scripts.

.DESCRIPTION
    Produces

        artifacts\DfsrAnalyser-<version>.zip          app\ (DfsrAnalyser.exe, dfsra.exe, the .NET runtime,
                                                      README.md, User-Guide.md), Install-DfsrAnalyser.ps1,
                                                      Uninstall-DfsrAnalyser.ps1, "Install DFSR Analyser.cmd",
                                                      README.txt, LICENSE, NOTICE.md
        artifacts\DfsrAnalyser-<version>.zip.sha256   "<hash>  <file>", sha256sum style

    A zip with an install script rather than an MSI: an unsigned MSI is warned about by SmartScreen on every
    release, and nothing here needs administrator rights. The one-line installer (install.ps1 at the repo
    root) downloads this zip and its .sha256 by exact name from the GitHub release, so attach both.

    Both programs are published into one folder so they share one copy of the runtime. The README and the
    user guide go next to the executable, where Help > Read me and Help > User guide look for them.

    The build stops if the staged files include anything that looks like case or log data (.duckdb, .log,
    .gz, .evtx, .csv): sample logs and cases stay on this machine.

.PARAMETER Version   e.g. 1.0.1; stamped into both programs and shown in Settings > Apps. Default: <Version> in
                     Directory.Build.props.
.EXAMPLE             .\tools\New-Package.ps1 -Version 1.0.1
#>
# Adapted from ADTD for PowerPoint's tools/New-Package.ps1.
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    $Version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if (-not $Version) { throw 'No <Version> in Directory.Build.props; pass -Version.' }
}

$artifacts = Join-Path $root 'artifacts'
$name = "DfsrAnalyser-$Version"
$stage = Join-Path $artifacts $name
$app = Join-Path $stage 'app'
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $app | Out-Null

foreach ($project in 'src\DfsrAnalyser.App\DfsrAnalyser.App.csproj', 'src\DfsrAnalyser.Cli\DfsrAnalyser.Cli.csproj') {
    Write-Host "Publishing $project ($Version)" -ForegroundColor Cyan
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -o $app `
        -p:Version=$Version -p:DebugType=none -p:GenerateDocumentationFile=false -nologo -v q
    if ($LASTEXITCODE) { throw "dotnet publish failed for $project" }
}
Get-ChildItem $app -Recurse -Filter *.pdb | Remove-Item -Force
foreach ($exe in 'DfsrAnalyser.exe', 'dfsra.exe') {
    if (-not (Test-Path (Join-Path $app $exe))) { throw "Publish produced no $exe" }
}
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'docs\User-Guide.md') $app

# Licence texts of every NuGet package the app ships (NOTICE.md lists them), from the package cache that the
# publish just restored: Apache-2.0 and zlib ask for the text to travel with the binaries.
$licenses = Join-Path $app 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$packages = foreach ($project in 'src\DfsrAnalyser.App', 'src\DfsrAnalyser.Cli') {
    $assets = Get-Content (Join-Path $root "$project\obj\project.assets.json") -Raw | ConvertFrom-Json
    $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Name }
}
foreach ($package in $packages | Sort-Object -Unique) {
    $id, $ver = $package -split '/'
    if ($id -match '\.(Linux|macOS)(\.|$)') { continue }   # native assets for other platforms are not shipped
    $folder = Join-Path $nuget "$($id.ToLowerInvariant())\$ver"
    Get-ChildItem $folder -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE|THIRD-PARTY)' } |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $licenses "$id-$($_.Name)") }
}
foreach ($required in 'Apache.Arrow-LICENSE.txt', 'DuckDB.NET.Bindings.Full-LICENSE-DuckDB.txt', 'OpenTK.redist.glfw-COPYING.md') {
    if (-not (Test-Path (Join-Path $licenses $required))) { throw "Licence text $required was not found in the NuGet cache ($nuget)." }
}

Copy-Item (Join-Path $PSScriptRoot 'Install-DfsrAnalyser.ps1'), (Join-Path $PSScriptRoot 'Uninstall-DfsrAnalyser.ps1'),
          (Join-Path $PSScriptRoot 'Install DFSR Analyser.cmd'), (Join-Path $root 'LICENSE'), (Join-Path $root 'NOTICE.md') $stage
@"
DFSR Analyser ${Version}: imports or collects DFS Replication debug logs from many servers and charts event
rates over time, down to individual events and the path of one file through every server.

To install, double-click "Install DFSR Analyser.cmd". It installs for you only (no administrator rights) into
%LOCALAPPDATA%\Programs\DFSR Analyser, adds it to the Start menu and puts the dfsra command on your PATH, and
lists it in Settings > Apps, where it can be uninstalled. Run Install-DfsrAnalyser.ps1 -NoPath from PowerShell
to leave PATH alone.

To run it without installing (on a server, say), start app\DfsrAnalyser.exe or app\dfsra.exe from here.

app\README.md gives an overview; app\User-Guide.md (Help > User guide in the app) explains how to use it.

DFSR Analyser is published under the MIT License (see LICENSE). Third-party components: see NOTICE.md.
Source and releases: https://github.com/MrR1974/DFSR-Analyser
"@ | Set-Content -Encoding UTF8 (Join-Path $stage 'README.txt')

# Nothing from an investigation leaves the machine: no cases, logs or exports.
$leaks = @(Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in '.duckdb', '.log', '.gz', '.evtx', '.csv', '.wal' })
if ($leaks) { throw "The release would include case or log data: $($leaks.FullName -join ', ')" }

$zip = Join-Path $artifacts "$name.zip"
Remove-Item -Force $zip, "$zip.sha256" -ErrorAction SilentlyContinue
# ZipFile writes '/' separators, which tar.exe (used by install.ps1) and Explorer both read.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content -Encoding ASCII -NoNewline "$zip.sha256"
$files = (Get-ChildItem $stage -Recurse -File).Count
Remove-Item -Recurse -Force $stage

Write-Host ''
Write-Host ("Wrote {0} ({1:N0} MB, {2} files)" -f $zip, ((Get-Item $zip).Length / 1MB), $files) -ForegroundColor Green
Write-Host "      $zip.sha256"
