<#
.SYNOPSIS
Runs the full AdventureWorks-compatible sample locally against SQLite:
create schema -> load 8 tables in dependency order -> per-table evaluations.

Requires a native SQLite library; Windows users can provision it with
scripts/setup-sqlite.ps1.
#>
[CmdletBinding()]
param(
    [string]$Database = "scratch/adventureworks.db"
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$ddl = Join-Path $PSScriptRoot 'adventureworks.sql'

$rules = @(
    '00-person.rules.yaml',
    '01-productcategory.rules.yaml',
    '02-productsubcategory.rules.yaml',
    '03-product.rules.yaml',
    '04-salesterritory.rules.yaml',
    '05-customer.rules.yaml',
    '06-salesorderheader.rules.yaml',
    '07-salesorderdetail.rules.yaml',
    '08-currency.rules.yaml',
    '09-employee.rules.yaml',
    '10-employeefinancials.rules.yaml'
)

# Fresh run: remove previous database files (main + per-schema attachments).
$dbFull = Join-Path $root $Database
$dbDir = Split-Path $dbFull -Parent
$dbBase = [IO.Path]::GetFileNameWithoutExtension($dbFull)
if (Test-Path $dbDir) {
    Remove-Item (Join-Path $dbDir "$dbBase*.db") -ErrorAction SilentlyContinue
}

$first = $true
foreach ($r in $rules) {
    $args = @(
        'run', '--project', (Join-Path $root 'src\SynthGen.Cli'), '--',
        'generate',
        '--ddl', $ddl,
        '--rules', (Join-Path $PSScriptRoot $r),
        '--provider', 'sqlite',
        '--connection', $dbFull
    )
    if ($first) { $args += '--create-table'; $first = $false }

    Write-Host "==> $r"
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        Write-Error "$r failed with exit code $LASTEXITCODE"
    }
}

Write-Host ""
Write-Host "AdventureWorks sample loaded into $dbFull — all evaluations passed."
