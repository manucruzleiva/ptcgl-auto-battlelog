<#
.SYNOPSIS
    Tests, builds and packages PTCGL Auto Battle Log into dist\ (Windows twin of build.sh).

.DESCRIPTION
    Same steps as build.sh, which CI and releases use: fetch and verify BepInEx, run the plugin core
    tests and the tray logic tests, check the plugin's sealed sample logs with tools\verify_log.py
    (and that tampered copies fail), build PtcglAutoBattleLog.exe, and write dist\:
    PtcglAutoBattleLog.exe, PtcglAutoBattleLog-portable.zip, update.json, SHA256SUMS.txt.

    Needs the .NET 8 SDK that global.json pins. Python 3.8+ (py -3, python3 or python) runs the seal cross-check and
    writes update.json / SHA256SUMS.txt; without it those steps are skipped with a warning.
    Works in Windows PowerShell 5.1 and PowerShell 7.

    Environment: VERSION, REPO_OWNER, REPO_NAME, DOTNET, PYTHON, SOURCE_DATE_EPOCH (see build.sh).

.PARAMETER Version
    X.Y.Z or X.Y.Z-beta.N. Default: the VERSION environment variable, else Directory.Build.props.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

.EXAMPLE
    .\build.ps1 1.2.0
#>
[CmdletBinding()]
param([string]$Version = $env:VERSION)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell's progress bar slows downloads a lot

# The BepInEx release embedded in the exe and installed into the game, pinned by hash. Change both
# together (and BepInExVersion in Directory.Build.props, and build.sh).
$BepInExVersion = '5.4.23.3'
$BepInExSha256 = '41a089e5b1b1f0713b331346baf6677b1184c69eabebf51101097954e854c749'
$BepInExZip = "BepInEx_win_x64_$BepInExVersion.zip"
$BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/$BepInExZip"

$ExeName = 'PtcglAutoBattleLog.exe'
$PortableZip = 'PtcglAutoBattleLog-portable.zip'
$SealSeparator = "`n`n-----BEGIN PTCGL-ABL SEAL v1-----`n"

$Root = $PSScriptRoot
$Dotnet = if ($env:DOTNET) { $env:DOTNET } else { 'dotnet' }
$env:DOTNET_NOLOGO = '1'
if (-not $env:DOTNET_CLI_TELEMETRY_OPTOUT) { $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1' }
$script:InGroup = $false
$script:InActions = ($env:GITHUB_ACTIONS -eq 'true')
# The tools print UTF-8 (test names, log names with emoji); have Windows PowerShell decode it as such.
$script:OldOutputEncoding = $null
try {
    $script:OldOutputEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
}
catch { }

# ------------------------------------------------------------------------------------- helpers

# Join-Path takes only one child in Windows PowerShell 5.1.
function P { return [IO.Path]::Combine([string[]]$args) }

function Close-Group {
    if ($script:InGroup) { Write-Host '::endgroup::'; $script:InGroup = $false }
}

function Write-Step([string]$Text) {
    Close-Group
    if ($script:InActions) { Write-Host "::group::$Text"; $script:InGroup = $true }
    else { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
}

# Runs a native program, showing its output. Native stderr must not become a terminating error
# under 'Stop' (Windows PowerShell does that when stderr is redirected), so the exit code decides.
function Invoke-Tool([string]$FilePath, [string[]]$Arguments, [switch]$AllowFailure) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $FilePath @Arguments | Out-Host
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $old }
    if (-not $AllowFailure -and $code -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $code"
    }
    return $code
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-BytesSha256([byte[]]$Data) {
    $h = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($h.ComputeHash($Data)) -replace '-', '').ToLowerInvariant() }
    finally { $h.Dispose() }
}

# Python 3.8+ as @{ Exe; Args }, or $null. Skips the Microsoft Store 'python' stub (exit 9009).
function Find-Python {
    $candidates = @()
    if ($env:PYTHON) { $candidates += @{ Exe = $env:PYTHON; Args = @() } }
    $candidates += @{ Exe = 'py'; Args = @('-3') }
    $candidates += @{ Exe = 'python3'; Args = @() }
    $candidates += @{ Exe = 'python'; Args = @() }
    foreach ($c in $candidates) {
        if (-not (Get-Command $c.Exe -ErrorAction SilentlyContinue)) { continue }
        $old = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $probe = @($c.Args) + @('-c', 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)')
            & $c.Exe @probe 2>$null | Out-Null
            $ok = ($LASTEXITCODE -eq 0)
        }
        catch { $ok = $false }
        finally { $ErrorActionPreference = $old }
        if ($ok) { return $c }
    }
    return $null
}

# Runs Python isolated (-I) and in UTF-8 mode (-X utf8: sample log names hold emoji and CJK).
function Invoke-Python([string[]]$Arguments, [switch]$AllowFailure) {
    $all = @($script:Python.Args) + @('-I', '-X', 'utf8') + $Arguments
    return Invoke-Tool $script:Python.Exe $all -AllowFailure:$AllowFailure
}

function Get-Prop([xml]$Props, [string]$Name) {
    $node = $Props.SelectSingleNode("/Project/PropertyGroup/$Name")
    if ($node) { return $node.InnerText.Trim() }
    return ''
}

# Flips one bit of the first character of a sealed log in $Folder: its seal no longer matches.
function Set-Tampered([string]$Folder, [string]$Kind) {
    foreach ($f in Get-ChildItem -LiteralPath $Folder -File | Where-Object { $_.Extension -eq '.txt' } | Sort-Object Name) {
        $hasSidecar = Test-Path -LiteralPath ([IO.Path]::ChangeExtension($f.FullName, '.seal'))
        $bytes = [IO.File]::ReadAllBytes($f.FullName)
        $text = [Text.Encoding]::UTF8.GetString($bytes)
        if (($Kind -eq 'sidecar') -ne $hasSidecar) { continue }
        if ($Kind -eq 'footer' -and -not $text.Contains($SealSeparator)) { continue }
        $i = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $i = 3 }
        $bytes[$i] = $bytes[$i] -bxor 1
        [IO.File]::WriteAllBytes($f.FullName, $bytes)
        return $f.Name
    }
    return $null
}

# ------------------------------------------------------------------------------------- main

Push-Location $Root
$work = P ([IO.Path]::GetTempPath()) ('ptcgl-build-' + [Guid]::NewGuid().ToString('N'))
try {
    [xml]$props = Get-Content -LiteralPath (P $Root 'Directory.Build.props') -Raw

    if (-not $Version) { $Version = Get-Prop $props 'Version' }
    if ($Version.StartsWith('v')) { $Version = $Version.Substring(1) }
    # UpdateChecker.TryParseVersion: three numbers without leading zeros, optionally a pre-release
    # suffix (1.3.2-beta.1), published as a GitHub pre-release (see build.sh).
    if ($Version -cnotmatch '^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$' -or $Version.Length -gt 40) {
        throw "Version must be MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-PRERELEASE (e.g. 1.2.0, 1.3.2-beta.1), got '$Version'"
    }
    $owner = if ($env:REPO_OWNER) { $env:REPO_OWNER } else { Get-Prop $props 'RepoOwner' }
    $repo = if ($env:REPO_NAME) { $env:REPO_NAME } else { Get-Prop $props 'RepoName' }
    foreach ($v in @($owner, $repo)) {
        # UpdateChecker.IsSafeName: ASCII letters, digits, '.', '_', '-', not starting with '.', at most 100.
        if ($v -cnotmatch '^[A-Za-z0-9_-][A-Za-z0-9._-]{0,99}$') { throw "Invalid GitHub owner/repository name '$v'" }
    }
    $propsBepInEx = Get-Prop $props 'BepInExVersion'
    if ($propsBepInEx -ne $BepInExVersion) {
        throw "Directory.Build.props has BepInExVersion $propsBepInEx but build.ps1 pins ${BepInExVersion}: update `$BepInExVersion and `$BepInExSha256 here and in build.sh"
    }
    if (-not (Get-Command $Dotnet -ErrorAction SilentlyContinue)) { throw "'$Dotnet' not found: install the .NET 8 SDK or set DOTNET" }
    $script:Python = Find-Python

    Write-Host "PTCGL Auto Battle Log v$Version - updates from github.com/$owner/$repo"
    # global.json pins the exact SDK (rollForward: disable): the same commit then builds the same exe.
    $sdkPinned = ((Get-Content -LiteralPath (P $Root 'global.json') -Raw) | ConvertFrom-Json).sdk.version
    if ((Invoke-Tool $Dotnet @('--version') -AllowFailure) -ne 0) {
        throw "The .NET SDK $sdkPinned that global.json pins is not installed: get it from https://dotnet.microsoft.com/download/dotnet/8.0"
    }
    if (-not $script:Python) {
        Write-Warning 'Python 3.8+ not found (py -3, python3, python): the seal cross-check, update.json and SHA256SUMS.txt are skipped.'
    }
    New-Item -ItemType Directory -Path $work | Out-Null

    # --------------------------------------------------------------------------------- BepInEx

    Write-Step "BepInEx ${BepInExVersion}: cache\$BepInExZip"
    $cacheDir = P $Root 'cache'
    $zipPath = P $cacheDir $BepInExZip
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    if (-not (Test-Path -LiteralPath $zipPath)) {
        Write-Host "Downloading $BepInExUrl"
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $partial = "$zipPath.partial"
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $partial -UseBasicParsing
        $actual = Get-Sha256 $partial
        if ($actual -ne $BepInExSha256) {
            Remove-Item -LiteralPath $partial -Force
            throw "Downloaded $BepInExZip has SHA-256 $actual, expected $BepInExSha256"
        }
        Move-Item -LiteralPath $partial -Destination $zipPath -Force
    }
    $actual = Get-Sha256 $zipPath
    if ($actual -ne $BepInExSha256) {
        throw "cache\$BepInExZip has SHA-256 $actual, expected $BepInExSha256 - delete it and run again"
    }
    Write-Host "SHA-256 OK ($BepInExSha256)"

    # The plugin compiles against the same BepInEx that ships to players. Written only when
    # missing or different, so an unchanged lib\ does not force a rebuild.
    Add-Type -AssemblyName System.IO.Compression
    $libDir = P $Root 'lib'
    New-Item -ItemType Directory -Force -Path $libDir | Out-Null
    $stream = [IO.File]::OpenRead($zipPath)
    try {
        $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Read)
        foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
            $entry = $archive.GetEntry("BepInEx/core/$name")
            if (-not $entry) { throw "$BepInExZip has no BepInEx/core/$name" }
            $buffer = New-Object IO.MemoryStream
            $src = $entry.Open()
            try { $src.CopyTo($buffer) } finally { $src.Dispose() }
            $data = $buffer.ToArray()
            $target = P $libDir $name
            if ((Test-Path -LiteralPath $target) -and (Get-Sha256 $target) -eq (Get-BytesSha256 $data)) { continue }
            [IO.File]::WriteAllBytes("$target.tmp", $data)
            Move-Item -LiteralPath "$target.tmp" -Destination $target -Force
            Write-Host "extracted lib\$name"
        }
        $archive.Dispose()
    }
    finally { $stream.Dispose() }
    Write-Host 'lib\BepInEx.dll, lib\0Harmony.dll ready'

    # --------------------------------------------------------------------------------- tests

    Write-Step 'Plugin core tests (tests\CoreTests), writing sample sealed logs'
    $samples = P $work 'seal-samples'
    New-Item -ItemType Directory -Path $samples | Out-Null
    $env:PTCGL_SEAL_SAMPLES = $samples
    try { Invoke-Tool $Dotnet @('run', '--project', (P $Root 'tests' 'CoreTests'), '-c', 'Release') | Out-Null }
    finally { Remove-Item Env:PTCGL_SEAL_SAMPLES -ErrorAction SilentlyContinue }
    if (-not (Get-ChildItem -LiteralPath $samples -File | Where-Object { $_.Extension -eq '.txt' })) {
        throw 'tests\CoreTests wrote no sample logs into PTCGL_SEAL_SAMPLES'
    }

    if ($script:Python) {
        $verifier = P $Root 'tools' 'verify_log.py'
        Write-Step 'Seal cross-check: tools\verify_log.py on the plugin''s sample logs'
        Invoke-Python @($verifier, '--self-test') | Out-Null
        Invoke-Python @($verifier, '--chain', $samples) | Out-Null

        Write-Step 'Seal cross-check: tampered copies must be rejected'
        foreach ($kind in @('footer', 'sidecar')) {
            $tampered = P $work "tampered-$kind"
            Copy-Item -LiteralPath $samples -Destination $tampered -Recurse
            $victim = Set-Tampered $tampered $kind
            if (-not $victim) { throw "No $kind-sealed sample log to tamper with" }
            $old = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $out = & $script:Python.Exe @($script:Python.Args + @('-I', '-X', 'utf8', $verifier, '--chain', $tampered)) 2>&1 | ForEach-Object { "$_" }
                $code = $LASTEXITCODE
            }
            finally { $ErrorActionPreference = $old }
            # Exit code 1 and a proper report (a crash would also exit 1).
            $report = ($out -join "`n")
            if ($code -ne 1 -or $report -notmatch '(?m)^INVALID ' -or $report -notmatch '(?m)^SUMMARY .*NOT VALID') {
                $out | Out-Host
                throw "tools\verify_log.py did not reject a tampered $kind seal ($victim): exit code $code, expected 1"
            }
            Write-Host "tampered $kind log rejected as expected: $victim"
        }
    }

    Write-Step 'Tray logic tests (tests\TrayTests)'
    Invoke-Tool $Dotnet @('run', '--project', (P $Root 'tests' 'TrayTests'), '-c', 'Release',
        "--property:Version=$Version", "--property:RepoOwner=$owner", "--property:RepoName=$repo") | Out-Null

    # --------------------------------------------------------------------------------- build

    Write-Step "Building $ExeName v$Version (plugin embedded)"
    Invoke-Tool $Dotnet @('build', (P $Root 'src' 'Tray' 'PtcglAutoBattleLog.Tray.csproj'), '-c', 'Release', '-nologo',
        "-p:Version=$Version", "-p:RepoOwner=$owner", "-p:RepoName=$repo") | Out-Null
    $exe = P $Root 'src' 'Tray' 'bin' 'Release' $ExeName
    if (-not (Test-Path -LiteralPath $exe)) { throw "$exe was not produced" }
    # The BepInEx zip is embedded, so a smaller exe means the payload is missing.
    if ((Get-Item -LiteralPath $exe).Length -le (Get-Item -LiteralPath $zipPath).Length) { throw "$exe is too small to contain its payload" }

    # --------------------------------------------------------------------------------- package

    Write-Step 'Packaging dist\'
    $dist = P $Root 'dist'
    if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
    New-Item -ItemType Directory -Path $dist | Out-Null
    $distExe = P $dist $ExeName
    Copy-Item -LiteralPath $exe -Destination $distExe

    # Same inputs, same zip: fixed timestamps (the HEAD commit's), fixed order.
    $epoch = $env:SOURCE_DATE_EPOCH
    if (-not $epoch -and (Get-Command git -ErrorAction SilentlyContinue)) {
        $old = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { $epoch = (& git -C $Root log -1 --format=%ct 2>$null | Select-Object -First 1) } catch { $epoch = $null }
        finally { $ErrorActionPreference = $old }
    }
    $seconds = 0L
    if (-not [long]::TryParse("$epoch", [ref]$seconds)) { $seconds = 0L }
    $seconds = [Math]::Max($seconds, 315532800L)   # zip dates start in 1980
    $stamp = [DateTimeOffset]::FromUnixTimeSeconds($seconds)

    $note = [IO.File]::ReadAllText((P $Root 'build' 'portable.txt'), [Text.Encoding]::UTF8)
    $note = $note.Replace('@PROJECT_URL@', "https://github.com/$owner/$repo").Replace('@VERSION@', $Version)
    $note = $note.Replace("`r`n", "`n").Replace("`n", "`r`n")   # Notepad-friendly

    # The license texts travel with the BepInEx and Doorstop binaries inside the exe (MIT and LGPL-2.1).
    $utf8 = New-Object Text.UTF8Encoding($false)
    function Get-NotepadText([string]$Path) {
        $text = [IO.File]::ReadAllText((P $Root $Path), [Text.Encoding]::UTF8)
        return $utf8.GetBytes($text.Replace("`r`n", "`n").Replace("`n", "`r`n"))
    }
    $zipOut = P $dist $PortableZip
    $fs = [IO.File]::Open("$zipOut.tmp", [IO.FileMode]::Create)
    try {
        $zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
        $files = @(
            @{ Name = 'PtcglAutoBattleLog/PtcglAutoBattleLog.exe'; Data = [IO.File]::ReadAllBytes($distExe) },
            @{ Name = 'PtcglAutoBattleLog/portable.txt'; Data = $utf8.GetBytes($note) },
            @{ Name = 'PtcglAutoBattleLog/LICENSE.txt'; Data = (Get-NotepadText 'LICENSE') },
            @{ Name = 'PtcglAutoBattleLog/THIRD-PARTY-NOTICES.txt'; Data = (Get-NotepadText 'THIRD-PARTY-NOTICES.md') },
            @{ Name = 'PtcglAutoBattleLog/licenses/LGPL-2.1.txt'; Data = (Get-NotepadText (P 'licenses' 'LGPL-2.1.txt')) }
        )
        foreach ($f in $files) {
            # Stored, like build.sh: the bytes do not depend on a compressor's version.
            $entry = $zip.CreateEntry($f.Name, [IO.Compression.CompressionLevel]::NoCompression)
            $entry.LastWriteTime = $stamp
            $s = $entry.Open()
            try { $s.Write($f.Data, 0, $f.Data.Length) } finally { $s.Dispose() }
        }
        $zip.Dispose()
    }
    finally { $fs.Dispose() }
    Move-Item -LiteralPath "$zipOut.tmp" -Destination $zipOut -Force

    if ($script:Python) {
        Write-Step 'Release manifest: dist\update.json, dist\SHA256SUMS.txt'
        Invoke-Python @((P $Root 'tools' 'make-manifest.py'), '--version', $Version, '--tag', "v$Version",
            '--owner', $owner, '--repo', $repo, '--exe', $distExe, '--portable-zip', $zipOut, '--out', $dist) | Out-Null
    }

    Close-Group
    Write-Host ''
    Write-Host "Done: PTCGL Auto Battle Log v$Version (updates from github.com/$owner/$repo)" -ForegroundColor Green
    foreach ($f in Get-ChildItem -LiteralPath $dist -File) { Write-Host ('  {0,-40} {1,10} bytes' -f ('dist\' + $f.Name), $f.Length) }
    if (-not $script:Python) { Write-Warning 'dist\ has no update.json / SHA256SUMS.txt and the seal cross-check did not run (no Python 3.8+).' }
}
catch {
    Close-Group
    if ($script:InActions) { Write-Host "::error::build.ps1: $($_.Exception.Message)" }
    Write-Host "build.ps1: error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    if ($script:OldOutputEncoding) { try { [Console]::OutputEncoding = $script:OldOutputEncoding } catch { } }
    Pop-Location
}
exit 0
