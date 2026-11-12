<#
    DFSR Analyser - remote installer

        irm https://raw.githubusercontent.com/MrR1974/DFSR-Analyser/main/install.ps1 | iex

    Downloads the latest release, checks it against its published SHA-256, and hands the extracted
    folder to the installer inside it (Install-DfsrAnalyser.ps1). Nothing is installed by this file
    itself: it is a courier.

    Why this rather than a downloaded installer
    -------------------------------------------
    SmartScreen's reputation check applies to executables the browser downloads, and an unsigned file
    has no reputation, so an unsigned MSI or setup.exe is warned about on every release. Fetching over
    HTTPS from inside PowerShell avoids that path entirely, and also avoids the mark of the web:
    Invoke-WebRequest writes no Zone.Identifier stream, so nothing extracted from the zip needs
    unblocking.

    What the hash does and does not prove
    -------------------------------------
    The zip and its checksum come from the same release, so this is an integrity check, not a
    supply-chain one: it catches a truncated or corrupted download, not a compromised repository. The
    trust anchor here is HTTPS and GitHub, exactly as it is for the script you are reading.

    Overrides, for the rare case you need one:

        $env:DFSRA_VERSION = '1.0.0'   # install a specific release instead of the latest
        $env:DFSRA_PATH    = 'D:\...'  # install somewhere other than %LOCALAPPDATA%\Programs\DFSR Analyser
        $env:DFSRA_NO_PATH = '1'       # do not put dfsra on the user PATH
#>
# Adapted from AlignPro's install.ps1.

# `irm | iex` runs this text inside the user's own interactive session, not as a script. Two things
# follow. `exit` there closes their PowerShell window, taking the error message with it, so nothing
# below exits unless this really is running as a file. And anything set at this level would stay set
# in their session afterwards, so it all lives in a script block, whose scope ends with it.
& {
    param([bool] $RunAsFile)

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    $repo = 'MrR1974/DFSR-Analyser'
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("DfsrAnalyser-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))

    function Report {
        param([string] $Message, [string] $Remedy)
        Write-Host ''
        Write-Host $Message -ForegroundColor Red
        if ($Remedy) { Write-Host $Remedy -ForegroundColor Yellow }
        Write-Host ''
    }

    # Says what went wrong, then stops by throwing, which the catch at the bottom recognises and does
    # not repeat. Install-DfsrAnalyser.ps1 reports its own failures the same way.
    function Fail {
        param([string] $Message, [string] $Remedy)
        Report $Message $Remedy
        $reported = [System.Exception]::new($Message)
        $reported.Data['DfsrAnalyser.Reported'] = $true
        throw $reported
    }

    Write-Host ''
    Write-Host 'DFSR Analyser' -ForegroundColor Cyan
    Write-Host ''

    # Windows PowerShell 5.1 still negotiates TLS 1.0 by default on some builds, which github.com refuses.
    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    catch {
        # .NET Core picks the protocol itself and the property may be read-only; nothing to do.
    }

    try {
        # --- find the release ------------------------------------------------------------------------
        $wanted = $env:DFSRA_VERSION
        $api = if ($wanted) {
            "https://api.github.com/repos/$repo/releases/tags/v$wanted"
        } else {
            "https://api.github.com/repos/$repo/releases/latest"
        }

        $headers = @{ 'User-Agent' = 'DfsrAnalyser-Installer'; 'Accept' = 'application/vnd.github+json' }
        $release = Invoke-RestMethod -Uri $api -Headers $headers -UseBasicParsing

        $version = $release.tag_name -replace '^v', ''
        Write-Host "  release                       $($release.tag_name)"

        $zipName = "DfsrAnalyser-$version.zip"
        $sumName = "$zipName.sha256"
        $zipAsset = $release.assets | Where-Object { $_.name -eq $zipName } | Select-Object -First 1
        $sumAsset = $release.assets | Where-Object { $_.name -eq $sumName } | Select-Object -First 1

        if (-not $zipAsset) { Fail "Release $($release.tag_name) has no asset named $zipName." 'The release may still be uploading. Try again shortly.' }
        if (-not $sumAsset) { Fail "Release $($release.tag_name) has no checksum asset named $sumName." 'Refusing to install something that cannot be checked.' }

        # --- download --------------------------------------------------------------------------------
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        $zipFile = Join-Path $staging $zipName
        $sumFile = Join-Path $staging $sumName

        # Invoke-WebRequest is markedly faster with the progress bar suppressed.
        $previousProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Write-Host ("  downloading                   {0:N0} MB" -f ($zipAsset.size / 1MB))
            Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipFile -Headers $headers -UseBasicParsing
            Invoke-WebRequest -Uri $sumAsset.browser_download_url -OutFile $sumFile -Headers $headers -UseBasicParsing
        }
        finally {
            $ProgressPreference = $previousProgress
        }

        # --- verify ----------------------------------------------------------------------------------
        # The checksum file is written sha256sum-style: "<hash>  <filename>".
        $expected = (((Get-Content -LiteralPath $sumFile -Raw) -split '\s+') | Where-Object { $_ })[0].ToLowerInvariant()
        # .NET rather than Get-FileHash, which Windows PowerShell started from PowerShell 7 can fail to find.
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $stream = [System.IO.File]::OpenRead($zipFile)
        try { $actual = -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($expected -ne $actual) {
            Fail "The download does not match its published checksum.`n  expected  $expected`n  got       $actual" `
                 'Nothing has been installed. This is usually an interrupted download - run it again.'
        }
        Write-Host "  verified                      SHA-256 matches the published checksum"

        # --- extract ---------------------------------------------------------------------------------
        # Windows' own tar.exe rather than Expand-Archive, which has been seen to hang on managed machines
        # while loading its compression assembly. The full path matters: Git for Windows puts its own tar
        # on PATH, and that one cannot read a zip.
        $extracted = Join-Path $staging 'package'
        $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (-not (Test-Path -LiteralPath $tar)) {
            Fail 'This version of Windows has no tar.exe to extract the download with.' `
                 "Download $zipName from https://github.com/$repo/releases, extract it, and run 'Install DFSR Analyser.cmd' inside it."
        }
        New-Item -ItemType Directory -Path $extracted -Force | Out-Null
        & $tar -xf $zipFile -C $extracted
        if ($LASTEXITCODE -ne 0) { throw "tar.exe could not extract $zipName (exit code $LASTEXITCODE)." }

        $installer = Join-Path $extracted 'Install-DfsrAnalyser.ps1'
        if (-not (Test-Path $installer)) { Fail 'The package does not contain Install-DfsrAnalyser.ps1.' 'The release asset looks wrong; please report it.' }

        # --- hand over -------------------------------------------------------------------------------
        # Run the installer as a script block built from its text rather than by path: executing a .ps1
        # file is governed by the execution policy, which is Restricted by default on Windows PowerShell,
        # while a script block created here is not.
        $script = [ScriptBlock]::Create((Get-Content -LiteralPath $installer -Raw))
        $arguments = @{ Source = $extracted; Version = $version }
        if ($env:DFSRA_PATH) { $arguments['InstallPath'] = $env:DFSRA_PATH }
        if ($env:DFSRA_NO_PATH) { $arguments['NoPath'] = $true }

        & $script @arguments
    }
    catch {
        if (-not $_.Exception.Data['DfsrAnalyser.Reported']) {
            Report "Installation failed: $($_.Exception.Message)" 'Nothing was left behind. Please report this if it repeats.'
        }
        if ($RunAsFile) { exit 1 }
    }
    finally {
        if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
    }
} ($MyInvocation.MyCommand.CommandType -eq 'ExternalScript')
