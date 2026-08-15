<#
.SYNOPSIS
The pfandwerk spine: walks GUARD -> SCAN -> PLAN -> GATE -> APPLY -> VERIFY -> REPORT.

.DESCRIPTION
Deliberately dumb. It sequences phases, checks exit codes and stops; it makes no decisions
of its own. Every decision lives either in deterministic C# (which values, which rows) or
with the human at the gate.

Two things it does NOT do, both on purpose:

  * It never starts `dab start`. A spine that silently launched a database-facing server
    would undo the guard phase that runs immediately before it. Start it yourself before
    using -Sink dab; the run aborts naming the URL if nothing is listening.
  * It never runs Revert. Hard rule 7 keeps revert manual and out of any automation.

The two Copilot steps are narrative only and run after the deterministic phase whose output
they describe. Skip them with -NoAgents and the run is still complete — you simply get no
prose.

.PARAMETER Provider
sqlserver (default) or sqlite. The SQLite path is for the offline fixture.

.PARAMETER Target
Target connection. Falls back to PFANDWERK_TARGET_CONNECTION.

.PARAMETER Ledger
Ledger connection. Falls back to PFANDWERK_LEDGER_CONNECTION, then to -Target.

.PARAMETER Only
Comma-separated rule ids to run. Everything else is left untouched.

.PARAMETER Yes
Approve without prompting. For scripted demonstration runs — a real run wants a human
reading the gate output.

.PARAMETER Sink
sql (default, one transaction covering the ledger and target writes) or dab (REST through a
running Data API Builder, for sites whose policy requires an API layer). On the dab path a
ledger row means reserved rather than applied — see dab/README.md.

.PARAMETER DabUrl
Base URL of the running DAB. Default http://localhost:5000.

.PARAMETER NoAgents
Skip both Copilot steps. No model spend; report.md falls back to the bare-facts rendering.

.PARAMETER WhatIf
Walk every phase and print what would happen without touching the database.

.EXAMPLE
./run.ps1 -Provider sqlite -Target scratch/fixture.db -Only SEC-001,SEC-002 -Yes

.EXAMPLE
./run.ps1 -WhatIf
#>
#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('sqlserver', 'sqlite')][string]$Provider = 'sqlserver',
    [string]$Target,
    [string]$Ledger,
    [string]$Only,
    [ValidateSet('sql', 'dab')][string]$Sink = 'sql',
    [string]$DabUrl,
    [string]$Artifacts = 'artifacts',
    [int]$Seed,
    [switch]$Yes,
    [switch]$NoAgents
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $common = @('--provider', $Provider, '--artifacts', $Artifacts)
    if ($Target) { $common += @('--target', $Target) }
    if ($Ledger) { $common += @('--ledger', $Ledger) }
    if ($Only)   { $common += @('--only', $Only) }
    if ($Sink)   { $common += @('--sink', $Sink) }
    if ($DabUrl) { $common += @('--dab-url', $DabUrl) }

    $agentFlags = @(
        '--available-tools=shell,write', '--allow-all-tools',
        '--add-dir', (Join-Path $PSScriptRoot $Artifacts),
        '--deny-url', '--no-color'
    )

    function Phase([string]$Name) { Write-Host "`n== $Name ==" -ForegroundColor Cyan }

    function Pf([string[]]$CliArgs, [int[]]$Ok = @(0)) {
        if ($WhatIfPreference) {
            Write-Host "  would run: pfandwerk $($CliArgs -join ' ')" -ForegroundColor DarkGray
            return 0
        }
        dotnet run --project src/Pfandwerk -v q -- @CliArgs | Out-Host
        if ($LASTEXITCODE -notin $Ok) { throw "pfandwerk $($CliArgs[0]) exited $LASTEXITCODE" }
        return $LASTEXITCODE
    }

    function Agent([string]$Prompt, [string]$Produces) {
        if ($NoAgents) { Write-Host "  skipped (-NoAgents): $Prompt" -ForegroundColor DarkGray; return $false }
        if ($WhatIfPreference) { Write-Host "  would run: copilot -p $Prompt" -ForegroundColor DarkGray; return $false }
        if (-not (Get-Command copilot -ErrorAction SilentlyContinue)) {
            Write-Host "  copilot not on PATH; skipping $Prompt" -ForegroundColor Yellow
            return $false
        }

        $model = @()
        if ($env:PFANDWERK_MODEL_MAKER) { $model = @('--model', $env:PFANDWERK_MODEL_MAKER) }
        & copilot -p $Prompt @agentFlags @model | Out-Host

        # copilot exits 0 even when it fails (e.g. unauthenticated), so the output file is
        # the only trustworthy success signal.
        if (-not (Test-Path $Produces)) {
            Write-Host "  $Prompt produced no $Produces" -ForegroundColor Yellow
            return $false
        }
        return $true
    }

    Phase 'GUARD + SCAN + PLAN'
    $planArgs = @('plan') + $common
    if ($PSBoundParameters.ContainsKey('Seed')) { $planArgs += @('--seed', "$Seed") }
    $null = Pf $planArgs

    Phase 'PLAN NARRATIVE (agent)'
    $null = Agent 'prompts/plan.md' (Join-Path $Artifacts 'plan-summary.md')

    Phase 'GATE'
    $approveArgs = @('approve') + $common
    if ($Yes) { $approveArgs += '--yes' }
    $null = Pf $approveArgs

    Phase 'APPLY'
    $null = Pf (@('apply') + $common)

    Phase 'VERIFY'
    $verify = Pf (@('verify') + $common) -Ok @(0, 10, 20, 30)

    Phase 'REPORT (agent writes, then deterministic audit)'
    # The agent writes report.md if it can; `report` then extracts the facts, audits
    # whatever is there, and falls back to the bare rendering if it is absent or fails.
    # Hard rule 8 lives in that one verb rather than in this script.
    $null = Agent 'prompts/report-maker.md' (Join-Path $Artifacts 'report.md')
    $null = Pf (@('report') + $common)

    Phase 'DONE'
    Write-Host "  verify exit code: $verify   (0 clean · 10 gaps remain · 20 invariant · 30 consumer)"
    Write-Host "  artifacts:        $Artifacts/"
    if ($verify -ne 0) { Write-Host '  VERIFY did not pass — read verify.json before shipping.' -ForegroundColor Yellow }
    exit $verify
}
finally { Pop-Location }
