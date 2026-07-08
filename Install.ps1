<#
.SYNOPSIS
    Installs TRUDUtilsD365 into every Visual Studio instance that has the
    Dynamics 365 Finance & Operations development tools.

.DESCRIPTION
    Discovers all Visual Studio installations (any version, edition or drive) via
    vswhere, a filesystem scan and the dynamics:// registry handler, keeps only the
    ones that actually contain the D365 F&O dev tools, downloads the TRUDUtilsD365
    binaries (or uses a local build) and copies them into each add-in folder.

    Does NOT rely on the DynamicsVSTools environment variable.
    Self-elevates to administrator when a copy into Program Files is required.

.PARAMETER Dev
    Install the latest development build from the master branch instead of the
    latest tagged release.

.PARAMETER Source
    Local folder that already contains TRUDUtilsD365.dll (and optionally .pdb).
    When supplied nothing is downloaded - handy for installing your own build.

.PARAMETER ListOnly
    Only discover and print the target folders. No download, copy or elevation.

.PARAMETER NoPause
    Do not wait for a key press before exiting.

.EXAMPLE
    # Latest release into every VS instance that has the D365 dev tools
    powershell -ExecutionPolicy Bypass -File .\Install.ps1

.EXAMPLE
    # See where it would install, without changing anything
    powershell -ExecutionPolicy Bypass -File .\Install.ps1 -ListOnly

.EXAMPLE
    # Install your own local build
    powershell -ExecutionPolicy Bypass -File .\Install.ps1 -Source .\TRUDUtilsD365\bin\Debug
#>
[CmdletBinding()]
param(
    [switch]$Dev,
    [string]$Source,
    [switch]$ListOnly,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$repo    = 'TrudAX/TRUDUtilsD365'
$files   = @('TRUDUtilsD365.dll', 'TRUDUtilsD365.pdb')
$marker  = 'Microsoft.Dynamics.Framework.Tools*.dll'

# Resolve -Source to a full path up front: after self-elevation the working directory
# changes (RunAs starts in System32), so a relative path would no longer resolve.
if ($Source) {
    $Source = (Resolve-Path -LiteralPath $Source -ErrorAction Stop).Path
}

function Test-D365Folder([string]$path) {
    return (Test-Path $path -PathType Container) -and
           ($null -ne (Get-ChildItem -Path $path -Filter $marker -File -ErrorAction SilentlyContinue | Select-Object -First 1))
}

# Download one file. The DLL is required (failure aborts); the .pdb is optional
# (a missing symbols file only warns, since the add-in runs fine without it).
function Get-File([string]$url, [string]$dest, [bool]$required) {
    try {
        Invoke-WebRequest $url -OutFile $dest -UseBasicParsing
    }
    catch {
        if ($required) { throw "Failed to download required file '$url': $($_.Exception.Message)" }
        Write-Warning "Optional file not downloaded ($([System.IO.Path]::GetFileName($dest))): $($_.Exception.Message)"
    }
}

function Get-D365AddinFolders {
    $folders = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    $add = {
        param($vsRoot)
        $ext = Join-Path $vsRoot 'Common7\IDE\Extensions'
        if (-not (Test-Path $ext)) { return }
        Get-ChildItem $ext -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $cand = Join-Path $_.FullName 'AddinExtensions'
            if (Test-D365Folder $cand) {
                [void]$folders.Add(([System.IO.Path]::GetFullPath($cand)).TrimEnd('\'))
            }
        }
    }

    # 1) vswhere - every installed VS instance, any drive / edition / version
    $pf86 = ${env:ProgramFiles(x86)}
    $vswhere = if ($pf86) { Join-Path $pf86 'Microsoft Visual Studio\Installer\vswhere.exe' } else { $null }
    if ($vswhere -and (Test-Path $vswhere)) {
        & $vswhere -all -prerelease -products * -property installationPath 2>$null |
            Where-Object { $_ -and $_.Trim() } |
            ForEach-Object { & $add $_.Trim() }
    }

    # 2) Filesystem fallback across the standard VS roots
    foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ } | Select-Object -Unique) {
        $base = Join-Path $pf 'Microsoft Visual Studio'
        if (-not (Test-Path $base)) { continue }
        Get-ChildItem $base -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue | ForEach-Object { & $add $_.FullName }
        }
    }

    # 3) dynamics:// registry handler hint (one VS install)
    try {
        $cmd = (Get-ItemProperty 'HKLM:\SOFTWARE\Classes\dynamics\shell\open\command' -ErrorAction Stop).'(default)'
        if ($cmd -match '"([^"]+\.exe)"') {
            $cand = Join-Path (Split-Path $Matches[1]) 'AddinExtensions'
            if (Test-D365Folder $cand) {
                [void]$folders.Add(([System.IO.Path]::GetFullPath($cand)).TrimEnd('\'))
            }
        }
    } catch { }

    return $folders
}

# --- discovery (read-only, no elevation needed) ---
$targets = @(Get-D365AddinFolders)
if ($targets.Count -eq 0) {
    throw 'No Visual Studio instance with the Dynamics 365 F&O development tools was found.'
}

Write-Host "Found $($targets.Count) target folder(s):" -ForegroundColor Cyan
$targets | ForEach-Object { Write-Host "  $_" }

if ($ListOnly) {
    if (-not $NoPause) { [void](Read-Host "`nPress Enter to exit") }
    return
}

# --- self-elevate for the copy into Program Files ---
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "`nElevation required - relaunching as administrator..." -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Dev)     { $argList += '-Dev' }
    if ($Source)  { $argList += @('-Source', "`"$Source`"") }
    if ($NoPause) { $argList += '-NoPause' }
    # -Wait + -PassThru so the elevated run's exit code propagates to the caller
    # (unattended scripting can then detect success/failure). A cancelled UAC prompt
    # throws, which we surface as a non-zero exit instead of an unhandled error.
    try {
        $proc = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList -PassThru -Wait
    }
    catch {
        Write-Warning "Elevation was cancelled or failed: $($_.Exception.Message)"
        exit 1
    }
    exit $proc.ExitCode
}

# --- resolve the binaries into a temp staging folder ---
$work = Join-Path $env:TEMP ('TRUDUtils_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    if ($Source) {
        if (-not (Test-Path (Join-Path $Source 'TRUDUtilsD365.dll'))) {
            throw "TRUDUtilsD365.dll not found in '$Source'."
        }
        foreach ($f in $files) {
            $s = Join-Path $Source $f
            if (Test-Path $s) { Copy-Item $s $work -Force }
        }
    }
    else {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        if ($Dev) {
            $baseUrl = "https://raw.githubusercontent.com/$repo/master/TRUDUtilsD365/bin/Debug"
            Write-Host "`nDownloading latest development build..." -ForegroundColor Cyan
            foreach ($f in $files) {
                Get-File "$baseUrl/$f" (Join-Path $work $f) ($f -eq 'TRUDUtilsD365.dll')
            }
        }
        else {
            # /releases/latest returns the latest *stable* release deterministically
            # (excludes prereleases and does not depend on list ordering).
            # Invoke-RestMethod parses the JSON response directly.
            $tag = (Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest").tag_name
            Write-Host "`nDownloading release $tag..." -ForegroundColor Cyan
            foreach ($f in $files) {
                Get-File "https://github.com/$repo/releases/download/$tag/$f" (Join-Path $work $f) ($f -eq 'TRUDUtilsD365.dll')
            }
        }
    }

    Get-ChildItem $work -File | ForEach-Object { Unblock-File $_.FullName }

    # --- copy into every discovered folder ---
    Write-Host "`nInstalling into $($targets.Count) VS instance(s):" -ForegroundColor Cyan
    $done = 0
    foreach ($t in $targets) {
        try {
            foreach ($f in (Get-ChildItem $work -File)) {
                Copy-Item $f.FullName (Join-Path $t $f.Name) -Force
            }
            Write-Host "  [OK]   $t" -ForegroundColor Green
            $done++
        }
        catch {
            Write-Warning "  [FAIL] $t : $($_.Exception.Message)"
        }
    }

    Write-Host "`nDone. Installed to $done/$($targets.Count) folder(s). Restart Visual Studio." -ForegroundColor Cyan
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not $NoPause) { [void](Read-Host "`nPress Enter to exit") }

# Non-zero exit when not every target was installed, so scripted callers can detect it.
if ($done -lt $targets.Count) { exit 1 }
