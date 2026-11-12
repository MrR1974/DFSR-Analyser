<#
.SYNOPSIS
    Builds a self-contained copy of DFSR Analyser (app + dfsra CLI) for testing inside an isolated lab VM.

.DESCRIPTION
    Publishes both executables for win-x64 as self-contained (no .NET install needed on the VM) into one
    folder, adds the README and the manual test checklist, and zips it. Copy the zip into the VM, extract, and run
    DfsrAnalyser.exe or dfsra.exe from that folder.

.PARAMETER Output
    Folder to publish into (default: .\artifacts\lab next to the solution).
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\lab')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Output = [IO.Path]::GetFullPath($Output)
$bin = Join-Path $Output 'DfsrAnalyser'

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force $bin | Out-Null

foreach ($project in 'src\DfsrAnalyser.App\DfsrAnalyser.App.csproj', 'src\DfsrAnalyser.Cli\DfsrAnalyser.Cli.csproj') {
    Write-Host "Publishing $project ..."
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -o $bin -p:DebugType=none -p:GenerateDocumentationFile=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

Copy-Item (Join-Path $root 'docs\Lab-Test-Checklist.md'), (Join-Path $root 'README.md') $bin
$zip = Join-Path $Output 'DfsrAnalyser-lab.zip'
Compress-Archive -Path $bin -DestinationPath $zip
$size = (Get-Item $zip).Length / 1MB
Write-Host ("Done: {0} ({1:N0} MB)" -f $zip, $size)
