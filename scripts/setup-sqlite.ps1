<#
.SYNOPSIS
Provisions the SQLite native library for SynthGen local runs and tests.

.DESCRIPTION
Enterprise policy: direct binary downloads are blocked, so SQLite may ONLY be
installed through conda or micromamba (conda-forge channel). This script refuses
any other method by design.

It locates an existing conda/micromamba installation, creates (or reuses) the
'synthgen-sqlite' environment with the sqlite package, and emits the resulting
sqlite3.dll path as its only pipeline output, so callers can capture it:

    $dll = & scripts\setup-sqlite.ps1 -Channel <url>

Re-runs are idempotent: an already-provisioned environment is reused as-is
(use -Force to recreate it).

.PARAMETER Channel
Conda channel to install from. Default: conda-forge (public). On restricted
networks pass your internal repository's conda remote, e.g.
  -Channel https://artifacts.corp.example/api/conda/conda-forge-remote
The channel is applied with --override-channels so a user-level .condarc can
never widen the source set.

.PARAMETER Offline
Air-gapped mode: no network at all. Resolves the environment purely from the
local conda package cache. Transfer the sqlite (+ vc/vc14_runtime/
vs2015_runtime/ucrt) archives into the cache first — see ENTERPRISE-SETUP.md.

.PARAMETER ProxyUrl
Outbound proxy for the conda invocation only (sets HTTP_PROXY/HTTPS_PROXY for
this process, restored afterwards). Nothing machine-wide is modified.

.PARAMETER CaBundlePath
PEM bundle for corporate TLS interception. Applied per-invocation via
CONDA_SSL_VERIFY and MAMBA_SSL_VERIFY (process scope, restored afterwards).

.PARAMETER PkgsDir
Extra conda package-cache directory (e.g. transferred archives for offline
mode). Prepended to CONDA_PKGS_DIRS for this invocation only.

.PARAMETER Force
Remove and recreate the 'synthgen-sqlite' environment even if it already
provides sqlite3.dll.

.PARAMETER SetUserEnvVar
Persist SYNTHGEN_SQLITE_DLL as a user environment variable. Manual convenience
only — the enterprise wizard never uses this; it pins the path per-process
instead.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$SetUserEnvVar,
    [string]$Channel = 'conda-forge',
    [switch]$Offline,
    [string]$ProxyUrl,
    [string]$CaBundlePath,
    [string]$PkgsDir,
    [switch]$Force
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

function Resolve-EnvDll {
    param([string]$EnvName)
    $rootCandidates = @(
        "$env:USERPROFILE\miniconda3",
        "$env:USERPROFILE\anaconda3",
        "$env:USERPROFILE\micromamba",
        "$env:LOCALAPPDATA\micromamba"
    )
    foreach ($root in $rootCandidates) {
        $candidate = Join-Path $root (Join-Path 'envs' (Join-Path $EnvName (Join-Path 'Library' (Join-Path 'bin' 'sqlite3.dll'))))
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

$envName = 'synthgen-sqlite'

$existing = Resolve-EnvDll -EnvName $envName
if ($existing -and -not $Force) {
    Write-Host "Env '$envName' already provisioned: $existing (use -Force to recreate)"
    if ($SetUserEnvVar) {
        [Environment]::SetEnvironmentVariable('SYNTHGEN_SQLITE_DLL', $existing, 'User')
        Write-Host "SYNTHGEN_SQLITE_DLL set for your user profile (new shells will see it)."
    }
    Write-Output $existing
    exit 0
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

$savedEnv = @{}
foreach ($name in 'HTTP_PROXY', 'HTTPS_PROXY', 'CONDA_SSL_VERIFY', 'MAMBA_SSL_VERIFY', 'CONDA_PKGS_DIRS') {
    $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
}

try {
    if ($ProxyUrl) {
        $env:HTTP_PROXY = $ProxyUrl
        $env:HTTPS_PROXY = $ProxyUrl
    }
    if ($CaBundlePath) {
        $env:CONDA_SSL_VERIFY = $CaBundlePath
        $env:MAMBA_SSL_VERIFY = $CaBundlePath
    }
    if ($PkgsDir) {
        if ($savedEnv['CONDA_PKGS_DIRS']) {
            $env:CONDA_PKGS_DIRS = "$PkgsDir,$($savedEnv['CONDA_PKGS_DIRS'])"
        }
        else {
            $env:CONDA_PKGS_DIRS = $PkgsDir
        }
    }

    if ($existing -and $Force) {
        Write-Host "Removing existing env '$envName' (-Force)..."
        & $tool.Path env remove -n $envName -y 2>$null | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            Write-Error "$($tool.Name) could not remove existing environment '$envName'."
        }
    }

    $createArgs = @('create', '-n', $envName, '-c', $Channel, '--override-channels', 'sqlite', '-y')
    if ($Offline) {
        $createArgs += '--offline'
        Write-Host "Offline mode: resolving from the local conda package cache only."
    }
    & $tool.Path @createArgs | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        if ($Offline) {
            Write-Error "$($tool.Name) failed to create environment '$envName'. Offline resolve failed - the package cache is missing archives. See ENTERPRISE-SETUP.md ('Conda without any network')."
        }
        else {
            Write-Error "$($tool.Name) failed to create environment '$envName'. If the channel is blocked, pass -Channel <internal conda remote>; test reachability with: & $($tool.Path) search -c $Channel --override-channels sqlite"
        }
    }
}
finally {
    foreach ($name in $savedEnv.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnv[$name])
    }
}

$dll = Resolve-EnvDll -EnvName $envName
if (-not $dll) {
    Write-Error "Environment created but sqlite3.dll was not found under envs\$envName."
}

Write-Host ""
Write-Host "SQLite ready: $dll"

if ($SetUserEnvVar) {
    [Environment]::SetEnvironmentVariable('SYNTHGEN_SQLITE_DLL', $dll, 'User')
    Write-Host "SYNTHGEN_SQLITE_DLL set for your user profile (new shells will see it)."
}
else {
    Write-Host "SynthGen auto-discovers this location; no environment variables were changed."
}

Write-Output $dll
