<#
.SYNOPSIS
End-to-end run of the dbo.Security example on a throwaway SQLite fixture.

.DESCRIPTION
Walks every phase in order and stops at the first failure, so each step can be verified
independently. No SQL Server, no network, no Copilot, no model spend — the two agent steps
are printed as the exact commands to run rather than invoked, because Copilot CLI is
local-only and needs your credentials.

Artifacts land in -Artifacts (default: artifacts/) and are the point of the exercise: read
gaps.json, plan.json, plan.approved, patches.jsonl, verify.json and facts.json between
steps.

.PARAMETER Fixture
SQLite database file to build. Recreated on every run.

.PARAMETER Artifacts
Directory for run artifacts. Cleared on every run.

.PARAMETER Seed
Seed for identity generation, so a demonstration run is reproducible. Omit for real runs.

.EXAMPLE
pwsh -File scripts/e2e-security.ps1
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Fixture   = 'scratch/security-e2e.db',
    [string]$Artifacts = 'artifacts',
    [int]$Seed         = 20260815
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $env:SYNTHGEN_SQLITE_DLL ??= '/usr/lib/x86_64-linux-gnu/libsqlite3.so.0'
    function Step([string]$Title) {
        Write-Host ''
        Write-Host "──────── $Title " -ForegroundColor Cyan
    }

    # Named parameters only: PowerShell would otherwise bind the first positional
    # argument to -Ok, and $Args collides with the automatic variable.
    function Pf([string[]]$CliArgs, [int[]]$Ok = @(0)) {
        # Out-Host: print the step's output instead of letting it flow into the
        # caller's pipeline, where it would be captured alongside the exit code.
        dotnet run --project src/Pfandwerk.Cli --no-build -v q -- @CliArgs | Out-Host
        if ($LASTEXITCODE -notin $Ok) { throw "pfandwerk $($CliArgs[0]) exited $LASTEXITCODE" }
        return $LASTEXITCODE
    }

    # ---------------------------------------------------------------- fixture

    Step 'FIXTURE — build a throwaway SQLite database'
    Remove-Item "$Fixture*" -Force -ErrorAction SilentlyContinue
    Remove-Item $Artifacts -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory -Path (Split-Path $Fixture), $Artifacts -Force
    # --only scopes the run to this fixture's table; the PROP-* rules target dbo.Property,
    # which this fixture does not create, and Scanner correctly refuses rules whose
    # columns are not in the live schema.
    $common = @('--provider', 'sqlite', '--target', $Fixture, '--artifacts', $Artifacts,
                '--only', 'SEC-001,SEC-002')

    dotnet run --project src/Pfandwerk.Cli --no-build -v q -- fixture @common 2>&1 |
        Where-Object { $_ } | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) { throw 'fixture build failed' }

    Step 'GUARD — connection allowlist'
    $null = Pf (@('guard') + $common)

    Step 'SCAN — detect gaps and capture the baseline'
    $null = Pf (@('scan') + $common)

    Step 'PLAN — freeze every value before anyone approves it'
    $null = Pf (@('plan') + $common + @('--seed', "$Seed"))

    Step 'AGENT 1 — plan narrative (run this yourself)'
    Write-Host '  copilot -p prompts/plan.md \' -ForegroundColor Yellow
    Write-Host '    --available-tools=shell,write --allow-all-tools \' -ForegroundColor Yellow
    Write-Host "    --add-dir `"$repo/$Artifacts`" --deny-url --no-color" -ForegroundColor Yellow
    Write-Host "  reads $Artifacts/plan.json -> writes $Artifacts/plan-summary.md"

    Step 'GATE — the human decision'
    $null = Pf (@('approve') + $common + @('--yes'))

    Step 'APPLY — ledger and target in one transaction'
    $null = Pf (@('apply') + $common)

    Step 'VERIFY — layers 1-3, diffed against the baseline'
    $verify = Pf (@('verify') + $common) -Ok @(0, 10, 20, 30)

    Step 'FACTS — deterministic ground truth for the report'
    $null = Pf (@('facts') + $common)

    Step 'AGENT 2 — report maker (run this yourself)'
    Write-Host '  copilot -p prompts/report-maker.md \' -ForegroundColor Yellow
    Write-Host '    --available-tools=shell,write --allow-all-tools \' -ForegroundColor Yellow
    Write-Host "    --add-dir `"$repo/$Artifacts`" --deny-url --no-color" -ForegroundColor Yellow
    Write-Host "  reads $Artifacts/facts.json -> writes $Artifacts/report.md"
    Write-Host '  then: pfandwerk audit' -ForegroundColor Yellow

    Step 'SUMMARY'
    Write-Host "  verify exit code: $verify  (0 = clean, 10/20/30 = L1/L2/L3 failure)"
    Write-Host "  artifacts:        $Artifacts/"
    Get-ChildItem $Artifacts | ForEach-Object { Write-Host ("    {0,-20} {1,6} bytes" -f $_.Name, $_.Length) }
    Write-Host ''
    Write-Host '  Both agent steps are printed, not run: Copilot CLI is local-only and' -ForegroundColor DarkGray
    Write-Host '  needs your credentials. Everything above them is deterministic.' -ForegroundColor DarkGray

    exit $verify
}
finally { Pop-Location }
