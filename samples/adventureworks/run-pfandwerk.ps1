<#
.SYNOPSIS
Runs the complete AdventureWorks demonstration:
generation -> deterministic corruption -> plan -> noninteractive approval -> apply ->
verify -> report audit.

The second pass deliberately reintroduces the same identity gaps. It proves that the
append-only ledger reuses the first pass values rather than minting replacements.
#>
[CmdletBinding()]
param(
    [string]$Database = "scratch/adventureworks.db",
    [string]$Artifacts = "scratch/adventureworks-artifacts"
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$db = Join-Path $root $Database
$artifactDir = Join-Path $root $Artifacts
$rules = Join-Path $PSScriptRoot 'gaps.yaml'
$checks = Join-Path $PSScriptRoot 'consumer-checks.yaml'

& (Join-Path $PSScriptRoot 'run-local.ps1') -Database $Database
if ($LASTEXITCODE -ne 0) { throw "AdventureWorks generation failed ($LASTEXITCODE)." }

dotnet run --project (Join-Path $root 'src\SynthGen.Cli') -- sample adventureworks-corrupt `
    --connection $db
if ($LASTEXITCODE -ne 0) { throw "AdventureWorks corruption failed ($LASTEXITCODE)." }

# run.ps1 translates -Yes to the CLI's noninteractive `patch approve --yes`.
& (Join-Path $root 'run.ps1') -Provider sqlite -Target $db -Rules $rules `
    -ConsumerChecks $checks -Artifacts $artifactDir -Seed 4242 -Yes -NoAgents
if ($LASTEXITCODE -ne 0) { throw "AdventureWorks patch run failed ($LASTEXITCODE)." }
$audit = Get-Content (Join-Path $artifactDir 'report.audit.json') -Raw | ConvertFrom-Json
if ($audit.Verdict -notin @('pass', 'fallback')) {
    throw "AdventureWorks report audit failed: $($audit.Verdict)"
}

# The same deterministic defects exercise identity reuse on a second approved run.
dotnet run --project (Join-Path $root 'src\SynthGen.Cli') -- sample adventureworks-corrupt `
    --connection $db
if ($LASTEXITCODE -ne 0) { throw "AdventureWorks second corruption failed ($LASTEXITCODE)." }

& (Join-Path $root 'run.ps1') -Provider sqlite -Target $db -Rules $rules `
    -ConsumerChecks $checks -Artifacts $artifactDir -Seed 9999 -Yes -NoAgents
if ($LASTEXITCODE -ne 0) { throw "AdventureWorks identity-reuse run failed ($LASTEXITCODE)." }

Write-Host "AdventureWorks pfandwerk orchestration completed: $artifactDir"
