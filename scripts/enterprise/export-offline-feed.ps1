<#
.SYNOPSIS
Exports the solution's complete NuGet dependency closure as a portable folder feed.

.DESCRIPTION
FROZEN since 2026-08: kept for air-gapped sites, not actively maintained.

Run this on a machine that has restored the solution at least once (connected to
nuget.org OR an internal mirror). It reads every project.assets.json, collects the
exact package closure (ids + pinned versions), and copies the .nupkg files from the
local global-packages cache into a folder feed you can carry to a restricted or
air-gapped machine (USB, internal share, artifact repository upload).

On the restricted machine, restore entirely from that folder:

    dotnet restore SynthGen.sln --source <path-to-offline-packages>

or wire it permanently via the NuGet.config that setup-enterprise.ps1 generates.
#>
[CmdletBinding()]
param(
    [string]$OutDir = "offline-packages"
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$globalPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }

$assetFiles = Get-ChildItem -Path $root -Recurse -Filter project.assets.json
if (-not $assetFiles) {
    Write-Error "No project.assets.json found. Run 'dotnet restore' once (connected) before exporting."
}

$packages = @{}
foreach ($file in $assetFiles) {
    $assets = Get-Content $file.FullName -Raw | ConvertFrom-Json
    foreach ($lib in $assets.libraries.PSObject.Properties) {
        if ($lib.Value.type -eq 'package') { $packages[$lib.Name] = $true }
    }
}

New-Item -ItemType Directory -Force (Join-Path $root $OutDir) | Out-Null
$copied = 0
$missing = @()
foreach ($key in $packages.Keys | Sort-Object) {
    $id, $version = $key -split '/'
    $nupkg = Join-Path $globalPackages "$($id.ToLower())\$version\$($id.ToLower()).$version.nupkg"
    if (Test-Path $nupkg) {
        Copy-Item $nupkg (Join-Path $root $OutDir)
        $copied++
    }
    else {
        $missing += $key
    }
}

Write-Host "Exported $copied package(s) to $OutDir"
if ($missing.Count -gt 0) {
    Write-Warning "Missing from local cache (restore first, then re-run): $($missing -join ', ')"
    exit 1
}
Write-Host ""
Write-Host "Verify on this machine (forces a from-scratch restore using only the feed):"
Write-Host "  dotnet restore SynthGen.sln --packages scratch\restore-test --source (Resolve-Path $OutDir) --no-cache"
