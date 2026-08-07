<#
.SYNOPSIS
Provisions the SQLite native library for SynthGen local runs and tests.

.DESCRIPTION
Enterprise policy: direct binary downloads are blocked, so SQLite may ONLY be
installed through conda or micromamba (conda-forge channel). This script refuses
any other method by design.

It locates an existing conda/micromamba installation, creates (or reuses) the
'synthgen-sqlite' environment with the sqlite package, and prints the resulting
sqlite3.dll path. SynthGen and the tests auto-discover that path; setting
SYNTHGEN_SQLITE_DLL explicitly is optional (-SetUserEnvVar does it for you).

.PARAMETER Channel
Conda channel to install from. Default: conda-forge (public). On restricted
networks pass your internal repository's conda remote, e.g.
  -Channel https://artifacts.corp.example/api/conda/conda-forge-remote
(or configure channel_alias once in .condarc — see .condarc.enterprise.example).

.PARAMETER Offline
Air-gapped mode: no network at all. Resolves the environment purely from the local
conda package cache. Transfer the sqlite (+ vc/vs2015_runtime/ucrt) .conda archives
into the cache first — see ENTERPRISE-SETUP.md.
#>
[CmdletBinding()]
param(
    # Persist SYNTHGEN_SQLITE_DLL as a user environment variable.
    [switch]$SetUserEnvVar,
    [string]$Channel = 'conda-forge',
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'

function Find-Tool {
    foreach ($name in 'micromamba', 'conda', 'mamba') {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return @{ Name = $name; Path = $cmd.Source } }
    }
    $probes = @(
        "$env:USERPROFILE\miniconda3\Scripts\conda.exe",
        "$env:USERPROFILE\anaconda3\Scripts\conda.exe",
        "$env:ProgramData\miniconda3\Scripts\conda.exe",
        "$env:LOCALAPPDATA\micromamba\micromamba.exe",
        "$env:USERPROFILE\micromamba\micromamba.exe"
    )
    foreach ($p in $probes) {
        if (Test-Path $p) {
            return @{ Name = [IO.Path]::GetFileNameWithoutExtension($p); Path = $p }
        }
    }
    return $null
}

$tool = Find-Tool
if (-not $tool) {
    Write-Error @"
No conda or micromamba installation found.
Per enterprise network policy, SQLite must be installed via conda or micromamba —
no direct binary downloads. Install Miniconda or micromamba through your approved
software channel, then re-run this script.
"@
}

Write-Host "Using $($tool.Name) at $($tool.Path)"

$envName = 'synthgen-sqlite'
$createArgs = @('create', '-n', $envName, '-c', $Channel, 'sqlite', '-y')
if ($Offline) {
    $createArgs += '--offline'
    Write-Host "Offline mode: resolving from the local conda package cache only."
}
& $tool.Path @createArgs
if ($LASTEXITCODE -ne 0) {
    $hint = if ($Offline) {
        "Offline resolve failed - the package cache is missing archives. See ENTERPRISE-SETUP.md ('Conda without any network')."
    } else {
        "If the public channel is blocked, pass -Channel <internal conda remote> or configure .condarc (see .condarc.enterprise.example)."
    }
    Write-Error "$($tool.Name) failed to create environment '$envName'. $hint"
}

# Resolve the env's sqlite3.dll (conda layout: envs/<name>/Library/bin on Windows).
$rootCandidates = @(
    (Split-Path (Split-Path $tool.Path -Parent) -Parent),   # <root>\Scripts\conda.exe -> <root>
    "$env:USERPROFILE\miniconda3",
    "$env:USERPROFILE\anaconda3",
    "$env:USERPROFILE\micromamba",
    "$env:LOCALAPPDATA\micromamba"
)
$dll = $null
foreach ($root in $rootCandidates) {
    $candidate = Join-Path $root "envs\$envName\Library\bin\sqlite3.dll"
    if (Test-Path $candidate) { $dll = $candidate; break }
}
if (-not $dll) { Write-Error "Environment created but sqlite3.dll was not found under envs\$envName." }

Write-Host ""
Write-Host "SQLite ready: $dll"

if ($SetUserEnvVar) {
    [Environment]::SetEnvironmentVariable('SYNTHGEN_SQLITE_DLL', $dll, 'User')
    Write-Host "SYNTHGEN_SQLITE_DLL set for your user profile (new shells will see it)."
}
else {
    Write-Host "SynthGen auto-discovers this location. To pin it explicitly:"
    Write-Host "  `$env:SYNTHGEN_SQLITE_DLL = '$dll'"
    Write-Host "or re-run with -SetUserEnvVar to persist it."
}
