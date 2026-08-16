<#
.SYNOPSIS
One-command enterprise setup for SynthGen: configure + provision + verify.

.DESCRIPTION
FROZEN since 2026-08: kept for sites on restricted networks, not actively maintained.
Neither CI uses it; scripts/setup-sqlite.ps1 is the maintained provisioning path.

Reconfigures every dependency for a restricted enterprise network in a single run,
then PROVES the environment works:

  PREFLIGHT  load enterprise-profile(.local).psd1, overlay flags, prompt only for gaps
  CONFIGURE  write the repo-root NuGet.config from the profile (mirror or offline feed)
  PROVISION  conda SQLite env via scripts/setup-sqlite.ps1 (channel/proxy/CA per-invocation)
  VERIFY     dotnet restore -> build -> full test suite, asserting zero skipped tests

Blast radius is repo + process scope only: no user-profile files, no user environment
variables, no system stores are ever modified.

Inputs, by precedence: command-line flags > enterprise-profile.local.psd1 (gitignored)
> enterprise-profile.psd1 (committed) > interactive prompt. With -NonInteractive the
wizard never prompts and fails fast on missing values.

Exit codes: 0 proven · 2 profile/flag validation · 3 prerequisite missing
· 4 NuGet.config conflict · 5 provision failed · 6 restore failed · 7 build failed
· 8 tests failed or skipped.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File scripts\enterprise\setup-enterprise.ps1

.EXAMPLE
scripts\enterprise\setup-enterprise.ps1 -Mode mirror -NuGetMirrorUrl https://artifacts.corp.example/api/nuget/v3/index.json -NonInteractive
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Mode = '',
    [string]$NuGetMirrorUrl,
    [string]$OfflineFeedPath,
    [string]$CondaChannelUrl,
    [string]$ProxyUrl,
    [string]$CaBundlePath,
    [switch]$NonInteractive,
    [switch]$SkipVerify,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Fail {
    param([int]$Code, [string]$Stage, [string]$Message)
    Write-Host ''
    Write-Host "FAILED [$Stage]"
    Write-Host $Message
    exit $Code
}

function Merge-Hashtable {
    param([hashtable]$Base, [hashtable]$Override)
    foreach ($key in @($Override.Keys)) {
        $value = $Override[$key]
        if ($value -is [hashtable] -and $Base[$key] -is [hashtable]) {
            Merge-Hashtable -Base $Base[$key] -Override $value
        }
        elseif ($value -is [string] -and $value -eq '') {
            continue
        }
        else {
            $Base[$key] = $value
        }
    }
}

function Get-TransferList {
    return @"
Produce the missing artifacts on any CONNECTED machine and carry them over your
approved transfer channel:
  [1] NuGet feed  : dotnet restore SynthGen.sln
                    pwsh scripts/enterprise/export-offline-feed.ps1
                    -> copy the resulting offline-packages\ folder (~95 MB, 123 .nupkg)
                       into the repo root here
  [2] Conda cache : conda create -n synthgen-sqlite -c conda-forge sqlite --download-only
                    -> copy the sqlite/vc/vc14_runtime/vs2015_runtime/ucrt archives from
                       that machine's pkgs cache into %USERPROFILE%\miniconda3\pkgs
                       (or into the folder named by Conda.OfflinePkgsDir)
Then re-run scripts\setup-enterprise.ps1.
"@
}

function Find-CondaTool {
    foreach ($name in 'micromamba', 'conda', 'mamba') {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    $probes = @(
        "$env:USERPROFILE\miniconda3\Scripts\conda.exe",
        "$env:USERPROFILE\anaconda3\Scripts\conda.exe",
        "$env:ProgramData\miniconda3\Scripts\conda.exe",
        "$env:LOCALAPPDATA\micromamba\micromamba.exe",
        "$env:USERPROFILE\micromamba\micromamba.exe"
    )
    foreach ($p in $probes) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

# ---------------------------------------------------------------- PREFLIGHT

$profilePath = Join-Path $root 'enterprise-profile.psd1'
$localPath = Join-Path $root 'enterprise-profile.local.psd1'

if (-not (Test-Path $profilePath)) {
    Fail 2 'PREFLIGHT' "enterprise-profile.psd1 not found at the repo root. Restore it from the repository."
}
try {
    $cfg = Import-PowerShellDataFile $profilePath
}
catch {
    Fail 2 'PREFLIGHT' "enterprise-profile.psd1 is not valid psd1: $($_.Exception.Message)"
}
if (Test-Path $localPath) {
    try {
        $local = Import-PowerShellDataFile $localPath
    }
    catch {
        Fail 2 'PREFLIGHT' "enterprise-profile.local.psd1 is not valid psd1: $($_.Exception.Message)"
    }
    Merge-Hashtable -Base $cfg -Override $local
    Write-Host "Loaded local override: enterprise-profile.local.psd1"
}

if ($PSBoundParameters.ContainsKey('Mode')) { $cfg.Mode = $Mode }
if ($PSBoundParameters.ContainsKey('NuGetMirrorUrl')) { $cfg.NuGet.MirrorUrl = $NuGetMirrorUrl }
if ($PSBoundParameters.ContainsKey('OfflineFeedPath')) { $cfg.NuGet.OfflineFeedPath = $OfflineFeedPath }
if ($PSBoundParameters.ContainsKey('CondaChannelUrl')) { $cfg.Conda.ChannelUrl = $CondaChannelUrl }
if ($PSBoundParameters.ContainsKey('ProxyUrl')) { $cfg.Network.ProxyUrl = $ProxyUrl }
if ($PSBoundParameters.ContainsKey('CaBundlePath')) { $cfg.Network.CaBundlePath = $CaBundlePath }

if ($cfg.ProfileVersion -ne 1) {
    Fail 2 'PREFLIGHT' "Unknown ProfileVersion '$($cfg.ProfileVersion)' (this wizard understands version 1). Update the wizard or fix the profile."
}
if ($cfg.Mode -ne '' -and $cfg.Mode -ne 'mirror' -and $cfg.Mode -ne 'offline') {
    Fail 2 'PREFLIGHT' "Mode must be 'mirror' or 'offline' (got '$($cfg.Mode)')."
}

$prompted = @{}

if (-not $cfg.Mode) {
    if ($NonInteractive) {
        Fail 2 'PREFLIGHT' 'Mode is not set. Pass -Mode mirror|offline or set Mode in enterprise-profile.psd1.'
    }
    while (-not $cfg.Mode) {
        $answer = Read-Host 'Select mode: [1] mirror (internal remotes)  [2] offline (folder feed + conda cache)'
        switch ($answer.Trim().ToLower()) {
            '1' { $cfg.Mode = 'mirror' }
            'mirror' { $cfg.Mode = 'mirror' }
            '2' { $cfg.Mode = 'offline' }
            'offline' { $cfg.Mode = 'offline' }
            default { Write-Host 'Please answer 1 or 2.' }
        }
    }
    $prompted['Mode'] = $cfg.Mode
}

if ($cfg.Mode -eq 'mirror') {
    if (-not $cfg.NuGet.MirrorUrl) {
        if ($NonInteractive) {
            Fail 2 'PREFLIGHT' 'NuGet.MirrorUrl is not set. Pass -NuGetMirrorUrl or set it in enterprise-profile.psd1 (mirror mode needs your internal NuGet v3 index URL).'
        }
        $attempts = 0
        while (-not $cfg.NuGet.MirrorUrl) {
            $attempts++
            if ($attempts -gt 3) {
                Fail 2 'PREFLIGHT' 'No valid NuGet mirror URL after 3 attempts.'
            }
            $answer = (Read-Host 'Internal NuGet v3 index URL (e.g. https://artifacts.corp.example/api/nuget/v3/index.json)').Trim()
            if ($answer -match '^https?://' -or (Test-Path $answer)) {
                $cfg.NuGet.MirrorUrl = $answer
                $prompted['MirrorUrl'] = $answer
            }
            else {
                Write-Host 'That does not look like an http(s) URL or an existing path.'
            }
        }
    }
    elseif (-not ($cfg.NuGet.MirrorUrl -match '^https?://' -or (Test-Path $cfg.NuGet.MirrorUrl))) {
        Fail 2 'PREFLIGHT' "NuGet.MirrorUrl '$($cfg.NuGet.MirrorUrl)' is neither an http(s) URL nor an existing folder feed path."
    }

    if (-not $cfg.Conda.ChannelUrl) {
        if ($NonInteractive) {
            $cfg.Conda.ChannelUrl = 'conda-forge'
            Write-Host 'WARNING: Conda.ChannelUrl is not set - using PUBLIC conda-forge. On a restricted network set it to your internal conda remote.'
        }
        else {
            $answer = (Read-Host 'Internal conda channel URL (Enter = public conda-forge)').Trim()
            if ($answer) {
                $cfg.Conda.ChannelUrl = $answer
                $prompted['ChannelUrl'] = $answer
            }
            else {
                $cfg.Conda.ChannelUrl = 'conda-forge'
                Write-Host 'WARNING: using PUBLIC conda-forge. On a restricted network set Conda.ChannelUrl to your internal conda remote.'
            }
        }
    }
}

if ($prompted.Count -gt 0 -and -not (Test-Path $localPath)) {
    $save = Read-Host 'Save these answers to enterprise-profile.local.psd1 for next time? [Y/n]'
    if ($save.Trim() -eq '' -or $save.Trim().ToLower() -eq 'y') {
        $lines = @('@{')
        $lines += '    # Written by setup-enterprise.ps1 from interactive answers. Gitignored.'
        if ($prompted.ContainsKey('Mode')) {
            $lines += "    Mode = '$($prompted['Mode'].Replace("'", "''"))'"
        }
        if ($prompted.ContainsKey('MirrorUrl')) {
            $lines += "    NuGet = @{ MirrorUrl = '$($prompted['MirrorUrl'].Replace("'", "''"))' }"
        }
        if ($prompted.ContainsKey('ChannelUrl')) {
            $lines += "    Conda = @{ ChannelUrl = '$($prompted['ChannelUrl'].Replace("'", "''"))' }"
        }
        $lines += '}'
        [IO.File]::WriteAllText($localPath, ($lines -join "`r`n") + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Saved enterprise-profile.local.psd1"
    }
}

# Prerequisites.
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Fail 3 'PREFLIGHT' '.NET SDK not found on PATH. Install .NET 10 (or newer) from the corporate software catalog (SCCM/Intune/winget internal source); nothing in this repo can download it.'
}
# Minimum, not exact: a newer SDK builds net10.0 fine, so pinning to one major
# would fail machines that are simply ahead of the fleet.
$MinSdkMajor = 10
$sdks = & dotnet --list-sdks
$hasMinSdk = $false
foreach ($line in $sdks) {
    if ("$line" -match '^(\d+)\.' -and [int]$Matches[1] -ge $MinSdkMajor) { $hasMinSdk = $true }
}
if (-not $hasMinSdk) {
    Fail 3 'PREFLIGHT' ".NET $MinSdkMajor SDK or newer not found (installed: $(($sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', ')). Install it from the corporate software catalog."
}

$condaTool = Find-CondaTool
if (-not $condaTool) {
    Fail 3 'PREFLIGHT' @"
No conda or micromamba installation found.
Per enterprise network policy, SQLite must be installed via conda or micromamba -
no direct binary downloads. Install Miniconda or micromamba through your approved
software channel, then re-run this wizard.
"@
}

$feedFull = $null
$feedCount = 0
if ($cfg.Mode -eq 'offline') {
    $feedFull = Join-Path $root $cfg.NuGet.OfflineFeedPath
    if (Test-Path $feedFull) {
        $feedCount = @(Get-ChildItem $feedFull -Filter *.nupkg -ErrorAction SilentlyContinue).Count
    }
    if (-not (Test-Path $feedFull) -or $feedCount -eq 0) {
        Fail 3 'PREFLIGHT' ("Offline mode, but the NuGet folder feed '$($cfg.NuGet.OfflineFeedPath)' is missing or empty.`r`n" + (Get-TransferList))
    }
}

if ($cfg.Network.CaBundlePath -and -not (Test-Path $cfg.Network.CaBundlePath)) {
    Fail 2 'PREFLIGHT' "Network.CaBundlePath '$($cfg.Network.CaBundlePath)' does not exist."
}
if ($cfg.Mode -eq 'mirror' -and $cfg.NuGet.MirrorUrl -match '^https://' -and -not $cfg.Network.CaBundlePath) {
    Write-Host 'NOTE: NuGet trusts the Windows certificate store. If TLS is intercepted, the corporate CA must already be installed there (this wizard never modifies system stores).'
}

Write-Host ''
Write-Host 'Effective configuration'
Write-Host "  Mode           : $($cfg.Mode)"
if ($cfg.Mode -eq 'mirror') {
    Write-Host "  NuGet source   : $($cfg.NuGet.MirrorUrl)"
    Write-Host "  Conda channel  : $($cfg.Conda.ChannelUrl)"
}
else {
    Write-Host "  NuGet source   : $($cfg.NuGet.OfflineFeedPath) ($feedCount packages)"
    if ($cfg.Conda.OfflinePkgsDir) {
        Write-Host "  Conda packages : $($cfg.Conda.OfflinePkgsDir) (+ local cache)"
    }
    else {
        Write-Host '  Conda packages : local cache only'
    }
}
if ($cfg.Network.ProxyUrl) { Write-Host "  Proxy          : $($cfg.Network.ProxyUrl)" }
if ($cfg.Network.CaBundlePath) { Write-Host "  CA bundle      : $($cfg.Network.CaBundlePath)" }
if ($cfg.NuGet.GlobalPackagesFolder) { Write-Host "  Package cache  : $($cfg.NuGet.GlobalPackagesFolder)" }
Write-Host ''

# ---------------------------------------------------------------- CONFIGURE

function Get-NuGetConfigContent {
    param([hashtable]$Cfg)
    $xml = New-Object System.Collections.Generic.List[string]
    $xml.Add('<?xml version="1.0" encoding="utf-8"?>')
    $xml.Add('<!-- Generated by scripts/enterprise/setup-enterprise.ps1 from enterprise-profile(.local).psd1.')
    $xml.Add('     Edit the profile and re-run the wizard instead of hand-editing this file. -->')
    $xml.Add('<configuration>')
    $xml.Add('  <packageSources>')
    $xml.Add('    <clear />')
    if ($Cfg.Mode -eq 'mirror') {
        $value = [System.Security.SecurityElement]::Escape($Cfg.NuGet.MirrorUrl)
        $xml.Add("    <add key=""corp-nuget"" value=""$value"" />")
    }
    else {
        $value = [System.Security.SecurityElement]::Escape($Cfg.NuGet.OfflineFeedPath)
        $xml.Add("    <add key=""offline"" value=""$value"" />")
    }
    $xml.Add('  </packageSources>')
    $configKeys = New-Object System.Collections.Generic.List[string]
    if ($Cfg.NuGet.GlobalPackagesFolder) {
        $value = [System.Security.SecurityElement]::Escape($Cfg.NuGet.GlobalPackagesFolder)
        $configKeys.Add("    <add key=""globalPackagesFolder"" value=""$value"" />")
    }
    if ($Cfg.Network.ProxyUrl) {
        $value = [System.Security.SecurityElement]::Escape($Cfg.Network.ProxyUrl)
        $configKeys.Add("    <add key=""http_proxy"" value=""$value"" />")
    }
    if ($configKeys.Count -gt 0) {
        $xml.Add('  <config>')
        foreach ($k in $configKeys) { $xml.Add($k) }
        $xml.Add('  </config>')
    }
    $xml.Add('</configuration>')
    return ($xml -join "`r`n") + "`r`n"
}

$nugetConfigPath = Join-Path $root 'NuGet.config'
$newContent = Get-NuGetConfigContent -Cfg $cfg

if (-not (Test-Path $nugetConfigPath)) {
    [IO.File]::WriteAllText($nugetConfigPath, $newContent, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host 'Wrote NuGet.config'
}
else {
    $existing = [IO.File]::ReadAllText($nugetConfigPath)
    if ($existing.Trim() -eq $newContent.Trim()) {
        Write-Host 'NuGet.config already up to date - leaving it untouched.'
    }
    else {
        $overwrite = $false
        if ($Force) {
            $overwrite = $true
        }
        elseif ($NonInteractive) {
            Fail 4 'CONFIGURE' 'NuGet.config exists and differs from the profile. Re-run with -Force to replace it (a NuGet.config.bak backup is kept), or reconcile enterprise-profile.psd1.'
        }
        else {
            Write-Host 'NuGet.config exists and differs from what the profile would generate.'
            $answer = Read-Host 'Overwrite NuGet.config (existing copied to NuGet.config.bak)? [y/N]'
            if ($answer.Trim().ToLower() -eq 'y') { $overwrite = $true }
        }
        if (-not $overwrite) {
            Fail 4 'CONFIGURE' 'Left the existing NuGet.config in place. Reconcile it with enterprise-profile.psd1, or re-run with -Force.'
        }
        Copy-Item $nugetConfigPath "$nugetConfigPath.bak" -Force
        [IO.File]::WriteAllText($nugetConfigPath, $newContent, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host 'Wrote NuGet.config (previous version in NuGet.config.bak)'
    }
}

# ------------------------------------------------- PROVISION + VERIFY + SUMMARY

$savedEnv = @{}
foreach ($name in 'HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'SYNTHGEN_SQLITE_DLL') {
    $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name)
}

try {
    if ($cfg.Network.ProxyUrl) {
        $env:HTTP_PROXY = $cfg.Network.ProxyUrl
        $env:HTTPS_PROXY = $cfg.Network.ProxyUrl
    }
    if ($cfg.Network.NoProxy) {
        $env:NO_PROXY = $cfg.Network.NoProxy
    }
    if ($cfg.Dotnet.TelemetryOptOut) {
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    }

    Write-Host ''
    Write-Host '== PROVISION: conda SQLite environment =='
    $sqliteArgs = @{}
    if ($cfg.Mode -eq 'offline') {
        $sqliteArgs['Offline'] = $true
    }
    else {
        $sqliteArgs['Channel'] = $cfg.Conda.ChannelUrl
    }
    if ($cfg.Network.ProxyUrl) { $sqliteArgs['ProxyUrl'] = $cfg.Network.ProxyUrl }
    if ($cfg.Network.CaBundlePath) { $sqliteArgs['CaBundlePath'] = $cfg.Network.CaBundlePath }
    if ($cfg.Conda.OfflinePkgsDir) { $sqliteArgs['PkgsDir'] = (Join-Path $root $cfg.Conda.OfflinePkgsDir) }

    $dll = $null
    try {
        $dll = & (Join-Path $PSScriptRoot '..\setup-sqlite.ps1') @sqliteArgs
    }
    catch {
        if ($cfg.Mode -eq 'offline') {
            Fail 5 'PROVISION' ("Conda could not create env 'synthgen-sqlite' offline: $($_.Exception.Message)`r`n" + (Get-TransferList))
        }
        else {
            Fail 5 'PROVISION' ("Conda could not create env 'synthgen-sqlite': $($_.Exception.Message)`r`nCheck Conda.ChannelUrl, Network.ProxyUrl and Network.CaBundlePath in the profile; test reachability with:`r`n  & `"$condaTool`" search -c $($cfg.Conda.ChannelUrl) --override-channels sqlite")
        }
    }
    if ($dll -is [object[]]) { $dll = $dll[$dll.Count - 1] }
    if (-not $dll -or -not (Test-Path "$dll")) {
        Fail 5 'PROVISION' "setup-sqlite.ps1 finished but did not yield a sqlite3.dll path (got '$dll')."
    }
    $env:SYNTHGEN_SQLITE_DLL = "$dll"
    Write-Host "SQLite pinned for this run: $dll"

    $passedCount = '?'
    if ($SkipVerify) {
        Write-Host ''
        Write-Host 'Verification skipped (-SkipVerify) - the environment is configured but UNVERIFIED.'
    }
    else {
        $sln = Join-Path $root 'SynthGen.sln'
        $auditArgs = @()
        if ($cfg.NuGet.DisableAudit) { $auditArgs += '-p:NuGetAudit=false' }

        Write-Host ''
        Write-Host '== VERIFY: dotnet restore =='
        & dotnet restore $sln @auditArgs
        if ($LASTEXITCODE -ne 0) {
            if ($cfg.Mode -eq 'mirror') {
                Fail 6 'VERIFY:RESTORE' "dotnet restore could not fetch packages through '$($cfg.NuGet.MirrorUrl)'. Check: (1) the mirror URL answers /index.json in a browser, (2) proxy settings, (3) the corporate CA is in the Windows trust store. NuGet.config <clear /> intentionally blocks all other sources."
            }
            else {
                Fail 6 'VERIFY:RESTORE' "restore from the '$($cfg.NuGet.OfflineFeedPath)' folder feed failed. The feed may be incomplete - regenerate it on a connected machine with scripts/enterprise/export-offline-feed.ps1 and re-copy. NU1603 'approximate best match' warnings are expected and benign."
            }
        }

        Write-Host ''
        Write-Host '== VERIFY: dotnet build =='
        & dotnet build $sln --no-restore @auditArgs
        if ($LASTEXITCODE -ne 0) {
            Fail 7 'VERIFY:BUILD' "build failed after a successful restore. Confirm an 8.x SDK appears in 'dotnet --list-sdks'; then rebuild with detailed output: dotnet build SynthGen.sln --no-restore -v n"
        }

        Write-Host ''
        Write-Host '== VERIFY: dotnet test =='
        $testOut = & dotnet test $sln --no-build @auditArgs | ForEach-Object { Write-Host $_; $_ }
        if ($LASTEXITCODE -ne 0) {
            Fail 8 'VERIFY:TEST' 'the test suite failed. Re-run with: dotnet test SynthGen.sln --no-build --logger "console;verbosity=detailed"'
        }
        $skipMatch = $testOut | Select-String -Pattern 'Skipped:\s*(\d+)' | Select-Object -Last 1
        if ($skipMatch) {
            $skipped = [int]$skipMatch.Matches[0].Groups[1].Value
            if ($skipped -gt 0) {
                Fail 8 'VERIFY:TEST' "$skipped test(s) were SKIPPED - the SQLite provisioning did not take effect for the test run (SYNTHGEN_SQLITE_DLL was '$dll'). Verify the file exists and re-run."
            }
        }
        else {
            Write-Host 'warning: could not parse the test summary to confirm zero skipped tests.'
        }
        $passMatch = $testOut | Select-String -Pattern 'Passed:\s*(\d+)' | Select-Object -Last 1
        if ($passMatch) { $passedCount = $passMatch.Matches[0].Groups[1].Value }
    }

    Write-Host ''
    Write-Host '================================================='
    if ($SkipVerify) {
        Write-Host 'ENVIRONMENT CONFIGURED (UNVERIFIED - ran with -SkipVerify)'
    }
    else {
        Write-Host 'ENVIRONMENT PROVEN'
    }
    Write-Host "  Mode          : $($cfg.Mode)"
    if ($cfg.Mode -eq 'mirror') {
        Write-Host "  NuGet source  : $($cfg.NuGet.MirrorUrl)"
    }
    else {
        Write-Host "  NuGet source  : $($cfg.NuGet.OfflineFeedPath) ($feedCount packages)"
    }
    Write-Host "  sqlite3.dll   : $dll"
    if (-not $SkipVerify) {
        Write-Host "  Verification  : restore OK, build OK, $passedCount tests passed (0 skipped)"
    }
    Write-Host '  Next          : pwsh samples/adventureworks/run-local.ps1   (generates scratch/adventureworks.db)'
    Write-Host '================================================='
    exit 0
}
finally {
    foreach ($name in $savedEnv.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnv[$name])
    }
}
