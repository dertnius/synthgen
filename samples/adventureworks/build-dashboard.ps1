<#
.SYNOPSIS
Builds the static GitHub Pages dashboard for the AdventureWorks pfandwerk E2E run.

.DESCRIPTION
Reads whatever run-pfandwerk.ps1 left behind (patch artifacts + SQLite databases),
copies every file into a self-contained site directory, and renders index.html:
run verdict tiles, per-check results, the rule table, and a linked inventory of
every artifact. Missing files are shown as missing rather than hidden, so a failed
run still produces an honest, deployable page.

.PARAMETER Artifacts
Directory with the patch-run artifacts (plan.json, verify.json, ...), relative to repo root.

.PARAMETER Scratch
Directory holding the adventureworks*.db SQLite files, relative to repo root.

.PARAMETER Output
Site output directory, relative to repo root. Deployed as-is to GitHub Pages.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Artifacts = 'scratch/adventureworks-artifacts',
    [string]$Scratch = 'scratch',
    [string]$Output = 'scratch/pages'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$artifactDir = Join-Path $root $Artifacts
$scratchDir = Join-Path $root $Scratch
$outDir = Join-Path $root $Output

New-Item -ItemType Directory -Force (Join-Path $outDir 'artifacts') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $outDir 'databases') | Out-Null

function Html([object]$s) { [System.Net.WebUtility]::HtmlEncode([string]$s) }

function Read-JsonFile([string]$Path) {
    if (Test-Path $Path) { Get-Content $Path -Raw | ConvertFrom-Json } else { $null }
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1MB) { '{0:n1} MB' -f ($Bytes / 1MB) }
    elseif ($Bytes -ge 1KB) { '{0:n1} KB' -f ($Bytes / 1KB) }
    else { "$Bytes B" }
}

# Status chip: color only ever appears on the icon; the word carries the meaning.
function Chip([string]$Kind, [string]$Label) {
    $icon = switch ($Kind) {
        'good' { '&#10003;' }
        'warning' { '&#9888;' }
        'critical' { '&#10007;' }
        default { '&#8212;' }
    }
    "<span class=""chip""><span class=""dot $Kind"">$icon</span>$(Html $Label)</span>"
}

function BoolChip([object]$Value, [string]$PassLabel = 'pass', [string]$FailLabel = 'fail') {
    if ($null -eq $Value) { Chip 'missing' 'missing' }
    elseif ($Value) { Chip 'good' $PassLabel }
    else { Chip 'critical' $FailLabel }
}

# ---- Collect inputs -------------------------------------------------------

$verify = Read-JsonFile (Join-Path $artifactDir 'verify.json')
$audit = Read-JsonFile (Join-Path $artifactDir 'report.audit.json')
$plan = Read-JsonFile (Join-Path $artifactDir 'plan.json')
$facts = Read-JsonFile (Join-Path $artifactDir 'facts.json')
$approved = Read-JsonFile (Join-Path $artifactDir 'plan.approved')

$patchesPath = Join-Path $artifactDir 'patches.jsonl'
$patchCount = if (Test-Path $patchesPath) { @(Get-Content $patchesPath).Count } else { $null }

$runId = if ($facts) { $facts.runId } elseif ($plan) { $plan.runId } else { 'unknown' }

$serverUrl = if ($env:GITHUB_SERVER_URL) { $env:GITHUB_SERVER_URL } else { 'https://github.com' }
$repo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { $null }
$sha = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (git -C $root rev-parse HEAD 2>$null) }
$branch = if ($env:GITHUB_REF_NAME) { $env:GITHUB_REF_NAME } else { (git -C $root branch --show-current 2>$null) }
$runUrl = if ($repo -and $env:GITHUB_RUN_ID) { "$serverUrl/$repo/actions/runs/$($env:GITHUB_RUN_ID)" } else { $null }
$commitUrl = if ($repo -and $sha) { "$serverUrl/$repo/commit/$sha" } else { $null }
$generated = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + ' UTC'

# ---- Verdict tiles --------------------------------------------------------

$invariants = @()
$consumer = @()
if ($verify) {
    $invariants = @($verify.invariants)
    $consumer = @($verify.consumer)
}
$invPass = @($invariants | Where-Object { $_.passed }).Count
$conPass = @($consumer | Where-Object { $_.passed }).Count

$auditChip =
    if ($null -eq $audit) { Chip 'missing' 'missing' }
    elseif ($audit.verdict -eq 'pass') { Chip 'good' 'pass' }
    elseif ($audit.verdict -eq 'fallback') { Chip 'warning' 'fallback' }
    else { Chip 'critical' ([string]$audit.verdict) }

$tiles = @(
    @{ label = 'L1 gap re-scan'; value = (BoolChip ($verify ? $verify.l1 : $null) 'clean' 'gaps remain'); sub = 'no planned gap still open' }
    @{ label = 'L2 invariants'; value = (BoolChip ($verify ? $verify.l2 : $null)); sub = "$invPass of $($invariants.Count) passed" }
    @{ label = 'L3 consumer checks'; value = (BoolChip ($verify ? $verify.l3 : $null)); sub = "$conPass of $($consumer.Count) passed" }
    @{ label = 'Report audit'; value = $auditChip; sub = 'report.md audited against facts.json' }
    @{ label = 'Patches applied'; value = "<span class=""num"">$(if ($null -ne $patchCount) { $patchCount } else { '&#8212;' })</span>"; sub = 'rows in the append-only ledger' }
    @{ label = 'Rules planned'; value = "<span class=""num"">$(if ($plan) { @($plan.rules).Count } else { '&#8212;' })</span>"; sub = 'gap rules in plan.json' }
)
$tileHtml = ($tiles | ForEach-Object {
    "<div class=""tile""><div class=""tlabel"">$(Html $_.label)</div><div class=""tvalue"">$($_.value)</div><div class=""tsub"">$(Html $_.sub)</div></div>"
}) -join "`n"

# ---- Check rows -----------------------------------------------------------

$checkRows = New-Object System.Collections.Generic.List[string]
foreach ($c in $invariants) {
    $checkRows.Add("<tr><td>invariant</td><td class=""mono"">$(Html $c.name)</td><td>$(BoolChip $c.passed)</td></tr>")
}
foreach ($c in $consumer) {
    $checkRows.Add("<tr><td>consumer</td><td class=""mono"">$(Html $c.name)</td><td>$(BoolChip $c.passed)</td></tr>")
}
if ($checkRows.Count -eq 0) {
    $checkRows.Add("<tr><td colspan=""3"">$(Chip 'missing' 'verify.json missing — the run did not reach VERIFY')</td></tr>")
}

# ---- Rule rows ------------------------------------------------------------

$ruleRows = New-Object System.Collections.Generic.List[string]
if ($plan) {
    foreach ($r in @($plan.rules)) {
        $statusChip = if ($r.status -eq 'OK') { Chip 'good' 'OK' } else { Chip 'warning' ([string]$r.status) }
        $ruleRows.Add("<tr><td class=""mono"">$(Html $r.id)</td><td class=""mono"">$(Html $r.table)</td><td class=""mono"">$(Html $r.column)</td><td>$(Html $r.kind)</td><td>$statusChip</td><td class=""num-cell"">$(@($r.patches).Count)</td><td class=""num-cell"">$(@($r.skipped).Count)</td></tr>")
    }
}
if ($ruleRows.Count -eq 0) {
    $ruleRows.Add("<tr><td colspan=""7"">$(Chip 'missing' 'plan.json missing — the run did not reach PLAN')</td></tr>")
}

# ---- Artifact inventory ---------------------------------------------------

$artifactCatalog = [ordered]@{
    'plan.json' = @('SCAN + PLAN', 'Full patch plan: every rule, the gaps found, and the exact values proposed per row.')
    'baseline.json' = @('SCAN', 'Invariant results before patching — red here is the deliberate corruption being confirmed.')
    'plan.approved' = @('GATE', 'Approval receipt: SHA-256 of the approved plan, approver, and timestamp.')
    'patches.jsonl' = @('APPLY', 'Append-only ledger of applied cell patches: one line per cell, old value to new value.')
    'verify.json' = @('VERIFY', 'Post-apply verification: L1 gap re-scan, L2 invariants, L3 consumer checks, regressions.')
    'facts.json' = @('REPORT', 'Deterministic facts extracted from the run — the ground truth the report is audited against.')
    'report.md' = @('REPORT', 'Human-readable run report (agent narrative, or the bare-facts fallback rendering).')
    'report.audit.json' = @('REPORT', 'Deterministic audit of report.md against facts.json — verdict and any violations.')
}

$artifactRows = New-Object System.Collections.Generic.List[string]
foreach ($name in $artifactCatalog.Keys) {
    $phase, $desc = $artifactCatalog[$name]
    $src = Join-Path $artifactDir $name
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $outDir 'artifacts' $name) -Force
        $size = Format-Size (Get-Item $src).Length
        $raw = Get-Content $src -Raw
        $preview = "<details><summary>preview</summary><pre>$(Html $raw)</pre></details>"
        $artifactRows.Add("<tr><td><span class=""phase"">$(Html $phase)</span></td><td class=""mono""><a href=""artifacts/$name"">$(Html $name)</a></td><td class=""num-cell"">$size</td><td>$(Html $desc)$preview</td></tr>")
    }
    else {
        $artifactRows.Add("<tr><td><span class=""phase"">$(Html $phase)</span></td><td class=""mono"">$(Html $name)</td><td class=""num-cell"">&#8212;</td><td>$(Html $desc) $(Chip 'missing' 'not produced')</td></tr>")
    }
}

$dbRows = New-Object System.Collections.Generic.List[string]
foreach ($db in @(Get-ChildItem $scratchDir -Filter 'adventureworks*.db' -File -ErrorAction SilentlyContinue | Sort-Object Name)) {
    Copy-Item $db.FullName (Join-Path $outDir 'databases' $db.Name) -Force
    $schema = if ($db.BaseName -match '\.(\w+)$') { "SQLite shard for the $($Matches[1]) schema." } else { 'Root SQLite database (attaches the schema shards).' }
    $dbRows.Add("<tr><td class=""mono""><a href=""databases/$($db.Name)"" download>$(Html $db.Name)</a></td><td class=""num-cell"">$(Format-Size $db.Length)</td><td>$(Html $schema)</td></tr>")
}
if ($dbRows.Count -eq 0) {
    $dbRows.Add("<tr><td colspan=""3"">$(Chip 'missing' 'no adventureworks*.db produced — generation did not run')</td></tr>")
}

# ---- Run metadata line ----------------------------------------------------

$metaParts = New-Object System.Collections.Generic.List[string]
$metaParts.Add("run <span class=""mono"">$(Html $runId)</span>")
if ($facts) { $metaParts.Add("approved by <span class=""mono"">$(Html $facts.approver)</span> at $(Html (([datetime]$facts.ts).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + ' UTC'))") }
elseif ($approved) { $metaParts.Add("approved by <span class=""mono"">$(Html $approved.osUser)</span>") }
if ($sha) {
    $shortSha = ([string]$sha).Substring(0, 7)
    $shaHtml = if ($commitUrl) { "<a class=""mono"" href=""$commitUrl"">$shortSha</a>" } else { "<span class=""mono"">$shortSha</span>" }
    $metaParts.Add("commit $shaHtml")
}
if ($branch) { $metaParts.Add("branch <span class=""mono"">$(Html $branch)</span>") }
if ($runUrl) { $metaParts.Add("<a href=""$runUrl"">workflow run</a> (full evidence bundle under Artifacts)") }
$runMeta = $metaParts -join ' &middot; '

# ---- Page -----------------------------------------------------------------

$template = @'
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>AdventureWorks E2E dashboard</title>
<style>
:root {
  color-scheme: light;
  --page: #f9f9f7; --surface: #fcfcfb;
  --ink: #0b0b0b; --ink-2: #52514e; --muted: #898781;
  --grid: #e1e0d9; --border: rgba(11,11,11,0.10);
  --good: #006300; --warning: #fab219; --critical: #d03b3b;
}
@media (prefers-color-scheme: dark) {
  :root {
    color-scheme: dark;
    --page: #0d0d0d; --surface: #1a1a19;
    --ink: #ffffff; --ink-2: #c3c2b7; --muted: #898781;
    --grid: #2c2c2a; --border: rgba(255,255,255,0.10);
    --good: #0ca30c; --warning: #fab219; --critical: #d03b3b;
  }
}
* { box-sizing: border-box; }
body {
  margin: 0; background: var(--page); color: var(--ink);
  font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif;
}
main { max-width: 1080px; margin: 0 auto; padding: 32px 20px 64px; }
h1 { font-size: 22px; margin: 0 0 4px; }
h2 { font-size: 16px; margin: 36px 0 10px; }
.sub, .tsub { color: var(--muted); font-size: 13px; }
.meta { color: var(--ink-2); font-size: 13px; margin-top: 6px; }
a { color: inherit; }
.mono { font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; font-size: 0.92em; }
.pipeline { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin: 20px 0 4px; color: var(--ink-2); font-size: 13px; }
.pipeline .step { border: 1px solid var(--border); background: var(--surface); border-radius: 6px; padding: 3px 10px; }
.pipeline .arrow { color: var(--muted); }
.tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 10px; margin-top: 16px; }
.tile { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; }
.tlabel { color: var(--ink-2); font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; }
.tvalue { margin: 6px 0 2px; font-size: 20px; }
.tvalue .num { font-size: 26px; }
.chip { display: inline-flex; align-items: center; gap: 6px; }
.dot { font-size: 0.85em; }
.dot.good { color: var(--good); }
.dot.warning { color: var(--warning); }
.dot.critical { color: var(--critical); }
.dot.missing { color: var(--muted); }
.tablewrap { overflow-x: auto; background: var(--surface); border: 1px solid var(--border); border-radius: 8px; }
table { border-collapse: collapse; width: 100%; font-size: 14px; }
th, td { text-align: left; padding: 8px 12px; border-top: 1px solid var(--grid); vertical-align: top; }
thead th { border-top: none; color: var(--ink-2); font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; }
.num-cell { font-variant-numeric: tabular-nums; white-space: nowrap; }
.phase { color: var(--ink-2); font-size: 12px; white-space: nowrap; }
details { margin-top: 4px; }
summary { cursor: pointer; color: var(--muted); font-size: 12px; }
details pre { background: var(--page); border: 1px solid var(--grid); border-radius: 6px; padding: 10px; overflow-x: auto; font-size: 12px; max-height: 320px; }
footer { margin-top: 40px; color: var(--muted); font-size: 12px; }
</style>
</head>
<body>
<main>
<h1>AdventureWorks E2E dashboard</h1>
<div class="sub">Every artifact from the pfandwerk run: generate &rarr; corrupt &rarr; plan &rarr; approve &rarr; apply &rarr; verify &rarr; report. The page reflects the second, identity-reuse pass &mdash; the same artifacts directory is written by both passes.</div>
<div class="meta">{{RUN_META}}</div>

<div class="pipeline">
  <span class="step">GUARD</span><span class="arrow">&rarr;</span>
  <span class="step">SCAN</span><span class="arrow">&rarr;</span>
  <span class="step">PLAN</span><span class="arrow">&rarr;</span>
  <span class="step">GATE</span><span class="arrow">&rarr;</span>
  <span class="step">APPLY</span><span class="arrow">&rarr;</span>
  <span class="step">VERIFY</span><span class="arrow">&rarr;</span>
  <span class="step">REPORT</span>
</div>

<div class="tiles">
{{TILES}}
</div>

<h2>Verification checks</h2>
<div class="tablewrap"><table>
<thead><tr><th>Layer</th><th>Check</th><th>Result</th></tr></thead>
<tbody>
{{CHECK_ROWS}}
</tbody></table></div>

<h2>Gap rules</h2>
<div class="tablewrap"><table>
<thead><tr><th>Rule</th><th>Table</th><th>Column</th><th>Kind</th><th>Status</th><th>Patched</th><th>Skipped</th></tr></thead>
<tbody>
{{RULE_ROWS}}
</tbody></table></div>

<h2>Run artifacts</h2>
<div class="tablewrap"><table>
<thead><tr><th>Phase</th><th>File</th><th>Size</th><th>What it is</th></tr></thead>
<tbody>
{{ARTIFACT_ROWS}}
</tbody></table></div>

<h2>Databases</h2>
<div class="tablewrap"><table>
<thead><tr><th>File</th><th>Size</th><th>What it is</th></tr></thead>
<tbody>
{{DB_ROWS}}
</tbody></table></div>

<footer>Generated {{GENERATED}} by samples/adventureworks/build-dashboard.ps1.</footer>
</main>
</body>
</html>
'@

$page = $template.
    Replace('{{RUN_META}}', $runMeta).
    Replace('{{TILES}}', $tileHtml).
    Replace('{{CHECK_ROWS}}', ($checkRows -join "`n")).
    Replace('{{RULE_ROWS}}', ($ruleRows -join "`n")).
    Replace('{{ARTIFACT_ROWS}}', ($artifactRows -join "`n")).
    Replace('{{DB_ROWS}}', ($dbRows -join "`n")).
    Replace('{{GENERATED}}', (Html $generated))

Set-Content -Path (Join-Path $outDir 'index.html') -Value $page -Encoding utf8
Write-Host "Dashboard written: $(Join-Path $outDir 'index.html')"
