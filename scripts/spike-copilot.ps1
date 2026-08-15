<#
.SYNOPSIS
P0b spike: probes the locally installed GitHub Copilot CLI and records what it
can actually do, so pfandwerk's agent design rests on evidence rather than
assumption.

.DESCRIPTION
pfandwerk's hard rule 6 ("agent tool access is deny-by-default") and its hooks
component both assume Copilot CLI supports pre/post-tool-use interception with
a real deny. That is a Claude Code concept; whether Copilot CLI offers an
equivalent is unverified, and the whole P4 phase depends on the answer.

This script answers three questions and writes them to a findings file:

  1. Can a hook — or any configuration — DENY a tool call, not merely log it?
  2. What is the -p / --prompt non-interactive contract: exit code, stdout
     shape, and whether prose is separable from tool output?
  3. Which model ids are selectable, and which is cheapest? The answer becomes
     the default for PFANDWERK_MODEL_MAKER.

It is deliberately discovery-oriented. Copilot CLI's flag surface is not
assumed: every probe is attempted, and a flag the CLI rejects is recorded as a
finding ("not supported") rather than treated as a script failure. Read the
generated file as a report, not a pass/fail gate.

Cost: one trivial model call ("reply with exactly ok") on the cheapest model
available, plus one more for the deny probe. It is testing the harness, not the
model, so nothing is gained by paying for a capable one. Pass -SkipModelCall to
probe the surface with no model spend at all.

Safety: model calls run in a scratch directory under the system temp path, not
in the repository, so a tool call that slips past a deny cannot touch tracked
files.

.PARAMETER Model
Model id for the probe calls. Omit to let the CLI use its default; the script
still reports whichever ids it can discover.

.PARAMETER OutFile
Findings file to write. Default: docs/copilot-cli-findings.md

.PARAMETER TimeoutSeconds
Per-probe timeout. Model calls can be slow on first run; the default is
generous because a timeout is itself a finding worth recording accurately.

.PARAMETER SkipModelCall
Probe the CLI surface (version, help, flags, config, hook support) without
spending anything on the model. Questions 2 and 3 are then answered only as far
as the help text allows.

.EXAMPLE
pwsh -File scripts/spike-copilot.ps1

.EXAMPLE
pwsh -File scripts/spike-copilot.ps1 -Model gpt-4o-mini -OutFile docs/findings.md
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Model,
    [string]$OutFile = 'docs/copilot-cli-findings.md',
    [int]$TimeoutSeconds = 180,
    [switch]$SkipModelCall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Exit codes match the repo convention (see src/SynthGen.Cli/CliSupport.cs).
$EXIT_OK = 0
$EXIT_PREREQ_MISSING = 3

$script:Probes = [System.Collections.Generic.List[object]]::new()

function Invoke-Probe {
    <#
    Runs a command, capturing stdout, stderr and exit code separately with a
    timeout. Never throws: a missing binary, a rejected flag and a hang are all
    outcomes worth recording, not reasons to abort the spike.
    #>
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $PWD.Path,
        [int]$Timeout = $TimeoutSeconds
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    foreach ($a in $Arguments) { [void]$psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false

    $started = [datetime]::UtcNow
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
    }
    catch {
        return [pscustomobject]@{
            Command = "$FilePath $($Arguments -join ' ')"
            ExitCode = $null; StdOut = ''; StdErr = $_.Exception.Message
            TimedOut = $false; Launched = $false; Duration = [timespan]::Zero
        }
    }

    # Read both streams asynchronously; a full pipe buffer would otherwise
    # deadlock a child that writes a lot of help text.
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()

    if (-not $proc.WaitForExit($Timeout * 1000)) {
        try { $proc.Kill($true) } catch { }
        return [pscustomobject]@{
            Command = "$FilePath $($Arguments -join ' ')"
            ExitCode = $null; StdOut = ''; StdErr = ''
            TimedOut = $true; Launched = $true
            Duration = [datetime]::UtcNow - $started
        }
    }

    [pscustomobject]@{
        Command  = "$FilePath $($Arguments -join ' ')"
        ExitCode = $proc.ExitCode
        StdOut   = $outTask.GetAwaiter().GetResult()
        StdErr   = $errTask.GetAwaiter().GetResult()
        TimedOut = $false
        Launched = $true
        Duration = [datetime]::UtcNow - $started
    }
}

function Add-Finding {
    param(
        [Parameter(Mandatory)][string]$Question,
        [Parameter(Mandatory)][string]$Title,
        [string]$Verdict,
        [string]$Notes,
        [object]$Probe
    )
    $script:Probes.Add([pscustomobject]@{
        Question = $Question; Title = $Title
        Verdict = $Verdict; Notes = $Notes; Probe = $Probe
    })
}

function Format-Block {
    param([string]$Text, [int]$MaxLines = 60)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '(empty)' }
    $lines = $Text -split "`r?`n"
    if ($lines.Count -gt $MaxLines) {
        $lines = $lines[0..($MaxLines - 1)] + "… ($($lines.Count - $MaxLines) more lines)"
    }
    $lines -join "`n"
}

# ---------------------------------------------------------------- locate CLI

$cli = Get-Command copilot -ErrorAction SilentlyContinue
if (-not $cli) {
    # Not Write-Error: ErrorActionPreference='Stop' would make it throw, and the
    # thrown error exits 1, masking the exit code callers switch on.
    [Console]::Error.WriteLine(@"
GitHub Copilot CLI ('copilot') is not on PATH.

Install it with:  npm install -g @github/copilot
Then authenticate (the CLI prompts on first interactive run) and re-run this script.
"@)
    exit $EXIT_PREREQ_MISSING
}
$copilot = $cli.Source
Write-Host "Copilot CLI: $copilot" -ForegroundColor Cyan

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "pfandwerk-spike-$(Get-Random)"
$null = New-Item -ItemType Directory -Path $scratch -Force

try {
    # ------------------------------------------------------------ surface

    $version = Invoke-Probe -FilePath $copilot -Arguments @('--version') -Timeout 60
    Add-Finding -Question 'Surface' -Title 'Version' -Verdict $version.StdOut.Trim() -Probe $version

    $help = Invoke-Probe -FilePath $copilot -Arguments @('--help') -Timeout 60
    $helpText = "$($help.StdOut)`n$($help.StdErr)"
    Add-Finding -Question 'Surface' -Title 'Top-level --help' -Probe $help

    # Which permission/hook-shaped flags does the help actually advertise? This
    # is the cheapest signal for question 1 and costs no model call.
    $flagPatterns = [ordered]@{
        'hooks'               = 'hook'
        'deny a single tool'  = '--deny-tool'
        'allow a single tool' = '--allow-tool'
        'allow all tools'     = '--allow-all-tools'
        'model selection'     = '--model'
        'non-interactive'     = '(^|\s)(-p|--prompt)(\s|,|$)'
        'permission mode'     = 'permission'
        'config directory'    = 'config'
    }
    $flagReport = foreach ($k in $flagPatterns.Keys) {
        $hit = [regex]::IsMatch($helpText, $flagPatterns[$k], 'IgnoreCase, Multiline')
        [pscustomobject]@{ Capability = $k; Pattern = $flagPatterns[$k]; InHelp = $hit }
    }
    Add-Finding -Question 'Surface' -Title 'Capability keywords in help' `
        -Notes (($flagReport | ForEach-Object { "- {0}: {1} (/{2}/)" -f $_.Capability, $(if ($_.InHelp) { 'present' } else { 'ABSENT' }), $_.Pattern }) -join "`n")

    # Config directory — hooks, if they exist, are usually declared here.
    $configDir = Join-Path $HOME '.copilot'
    $configNotes = if (Test-Path $configDir) {
        (Get-ChildItem -Path $configDir -Recurse -File -ErrorAction SilentlyContinue |
            Select-Object -First 40 |
            ForEach-Object { "- $($_.FullName.Substring($configDir.Length + 1))" }) -join "`n"
    } else { "No $configDir directory present." }
    Add-Finding -Question 'Surface' -Title 'Config directory contents' -Notes $configNotes

    # ------------------------------------------------- Q3: available models

    # No stable subcommand is assumed; each candidate is tried and its rejection
    # recorded. Whichever answers becomes the source for PFANDWERK_MODEL_MAKER.
    foreach ($candidate in @(@('--list-models'), @('models'), @('model', 'list'), @('--help', 'model'))) {
        $p = Invoke-Probe -FilePath $copilot -Arguments $candidate -Timeout 60
        Add-Finding -Question 'Q3 models' -Title "Try: copilot $($candidate -join ' ')" `
            -Verdict $(if ($p.ExitCode -eq 0) { 'accepted' } else { "rejected (exit $($p.ExitCode))" }) -Probe $p
    }

    # ------------------------------------- Q2: non-interactive -p contract

    $promptArgs = @('-p', 'Reply with exactly: ok')
    if ($Model) { $promptArgs += @('--model', $Model) }

    if ($SkipModelCall) {
        Add-Finding -Question 'Q2 -p contract' -Title 'Skipped' `
            -Verdict 'not probed' -Notes '-SkipModelCall was passed; no model call made.'
    }
    else {
        $run = Invoke-Probe -FilePath $copilot -Arguments $promptArgs -WorkingDirectory $scratch
        $verdict = if ($run.TimedOut) { "TIMED OUT after ${TimeoutSeconds}s" }
                   elseif ($run.ExitCode -eq 0) { 'exit 0' }
                   else { "exit $($run.ExitCode)" }
        $stdoutIsClean = -not $run.TimedOut -and
                         $run.StdOut.Trim().Length -gt 0 -and
                         $run.StdOut.Trim().Length -lt 200
        Add-Finding -Question 'Q2 -p contract' -Title 'copilot -p "Reply with exactly: ok"' `
            -Verdict $verdict `
            -Notes @"
stdout length: $($run.StdOut.Length) chars
stderr length: $($run.StdErr.Length) chars
duration: $([math]::Round($run.Duration.TotalSeconds, 1))s
stdout looks like bare prose (usable as a report body): $stdoutIsClean

If stdout carries tool chatter as well as prose, run-report.ps1 cannot pipe it
straight into report.md — it needs a separator, a --json style flag, or the
maker must write the file itself (which prompts/report-maker.md already asks
for, and is the safer contract either way).
"@ -Probe $run
    }

    # ------------------------------------------------ Q1: can a tool be denied

    # Probe: ask for a shell command while denying shell access. If the CLI
    # supports the flag AND honours it, the command must not run.
    $canaryName = 'pfandwerk-canary.txt'
    $canary = Join-Path $scratch $canaryName

    if ($SkipModelCall) {
        Add-Finding -Question 'Q1 deny' -Title 'Skipped' `
            -Verdict 'not probed' -Notes '-SkipModelCall was passed; no model call made.'
    }
    else {
        foreach ($denyFlag in @('--deny-tool', '--disallow-tool')) {
            if (Test-Path $canary) { Remove-Item $canary -Force }
            # Not $args — that is an automatic variable and assigning to it is a
            # trap under Set-StrictMode.
            $denyArgs = @($denyFlag, 'shell', '-p', "Run a shell command that creates a file called $canaryName in the current directory.")
            if ($Model) { $denyArgs += @('--model', $Model) }

            $p = Invoke-Probe -FilePath $copilot -Arguments $denyArgs -WorkingDirectory $scratch
            $created = Test-Path $canary
            $flagRejected = $p.ExitCode -ne 0 -and
                            [regex]::IsMatch("$($p.StdErr)$($p.StdOut)", 'unknown|unrecognized|invalid', 'IgnoreCase')

            $verdict = if ($flagRejected) { "flag not supported" }
                       elseif ($created) { "DENY NOT ENFORCED — canary file was created" }
                       else { "deny held — no canary file" }

            Add-Finding -Question 'Q1 deny' -Title "Deny probe via $denyFlag shell" -Verdict $verdict `
                -Notes @"
Canary present afterwards: $created

'deny held' is necessary but not sufficient: the model may simply have declined
to try. Read the transcript below and confirm it actually attempted the tool
call and was refused. If it never tried, re-run with a blunter prompt before
concluding hooks are unnecessary.
"@ -Probe $p

            if (-not $flagRejected) { break }
        }
    }

    # ------------------------------------------------------------- report

    $outPath = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path $PWD.Path $OutFile }
    $outDir = Split-Path -Parent $outPath
    if ($outDir -and -not (Test-Path $outDir)) { $null = New-Item -ItemType Directory -Path $outDir -Force }

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('# Copilot CLI findings (pfandwerk P0b spike)')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("Generated: $([datetime]::UtcNow.ToString('u')) by ``scripts/spike-copilot.ps1``")
    [void]$sb.AppendLine("Host: $([System.Environment]::OSVersion.VersionString), PowerShell $($PSVersionTable.PSVersion)")
    [void]$sb.AppendLine("CLI: ``$copilot``")
    $modelLabel = if ($Model) { "``$Model``" } else { 'CLI default' }
    [void]$sb.AppendLine("Model: $modelLabel")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Decisions this file drives')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- **Q1 → hard rule 6 and §6.18.** If no configuration can deny a tool call, hooks')
    [void]$sb.AppendLine('  cannot enforce deny-by-default and the agent must instead run process-isolated')
    [void]$sb.AppendLine('  with a read-only mount and no database route. §3 changes before P4 begins.')
    [void]$sb.AppendLine('- **Q2 → run-report.ps1.** Decides whether the maker''s stdout can be used directly')
    [void]$sb.AppendLine('  or whether the agent must write `report.md` itself.')
    [void]$sb.AppendLine('- **Q3 → PFANDWERK_MODEL_MAKER.** The cheapest listed id becomes the default.')
    [void]$sb.AppendLine()

    # Explicit order: Group-Object sorts alphabetically, which would bury the
    # free surface evidence under the probes that depend on reading it first.
    $questionOrder = @('Surface', 'Q1 deny', 'Q2 -p contract', 'Q3 models')
    $grouped = $script:Probes | Group-Object Question
    $ordered = @($questionOrder | ForEach-Object { $n = $_; $grouped | Where-Object Name -eq $n }) +
               @($grouped | Where-Object { $_.Name -notin $questionOrder })

    foreach ($group in $ordered) {
        [void]$sb.AppendLine("## $($group.Name)")
        [void]$sb.AppendLine()
        foreach ($f in $group.Group) {
            [void]$sb.AppendLine("### $($f.Title)")
            [void]$sb.AppendLine()
            if ($f.Verdict) { [void]$sb.AppendLine("**Verdict:** $($f.Verdict)"); [void]$sb.AppendLine() }
            if ($f.Notes) { [void]$sb.AppendLine($f.Notes); [void]$sb.AppendLine() }
            if ($f.Probe) {
                [void]$sb.AppendLine("``$($f.Probe.Command)`` → exit $($f.Probe.ExitCode)$(if ($f.Probe.TimedOut) { ' (TIMED OUT)' })")
                [void]$sb.AppendLine()
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine((Format-Block $f.Probe.StdOut))
                if ($f.Probe.StdErr.Trim()) {
                    [void]$sb.AppendLine('--- stderr ---')
                    [void]$sb.AppendLine((Format-Block $f.Probe.StdErr -MaxLines 20))
                }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine()
            }
        }
    }

    [void]$sb.AppendLine('## Conclusion')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('_Fill this in by hand after reading the transcripts above._')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- Hooks can deny tool calls: **yes / no**')
    [void]$sb.AppendLine('- Enforcement chosen for hard rule 6: **hooks / process isolation**')
    [void]$sb.AppendLine('- `-p` stdout usable as a report body: **yes / no**')
    [void]$sb.AppendLine('- Cheapest model id: **____**')

    Set-Content -Path $outPath -Value $sb.ToString() -Encoding utf8
    Write-Host "Findings written to $outPath" -ForegroundColor Green
    Write-Host 'Read it, complete the Conclusion section, and commit it before starting P4.' -ForegroundColor Yellow
    exit $EXIT_OK
}
finally {
    Remove-Item -Path $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
