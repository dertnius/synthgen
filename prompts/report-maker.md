# REPORT maker

Read `AGENTS.md` first. Its hard rules bind everything below.

## Task

Read `artifacts/facts.json` and write `artifacts/report.md`: the record of what this run
changed, read afterwards by people who were not at the gate.

If `artifacts/facts.json` does not exist or does not parse, write nothing. Say so and stop.

## Source — and nothing else

`artifacts/facts.json` is your only source. Every number, rule id, column name, sample
value, identity value, approver, hash and test name must appear in it.

`ReportAuditor` — deterministic C#, not a model — re-reads your output and checks that
every number and rule id in it appears in `facts.json`, and that every rule id in
`facts.json` appears in your output. A single invented or dropped figure fails the run, and
the report does not ship. Anything you cannot source, omit.

## Structure

**Header.** Run id, plan sha, rules sha, approver, timestamp — from `facts.json`.

**Per rule.** One `##` section per entry in `rules[]`, in file order: the rule id and
column, how many rows were patched, the `reason` verbatim, and 3 sample values quoted
exactly as they appear.

**New identities.** A table of every entry in `newIdentities[]`: row key, column, value.
State that these are permanent and will be reused by every future run. If the array is
empty, write `None.`

**Verify.** A table with one row per layer (l1, l2, l3) and its result. Then two clearly
separated lists:

- **Regressions** — from `verify.regressions`. These are tests this run broke.
- **Pre-existing failures** — from `verify.preexistingReds`. These were already failing
  before the run started and are not attributable to it.

Never merge those two lists, and never present a pre-existing failure as though this run
caused it. The distinction is the whole point of capturing a baseline, and conflating them
is the most damaging error you can make in this document.

## Tone

Factual and flat. No executive summary, no "successfully", no assessment of whether the run
went well. A reader wants to know what changed and what broke. If `verify.regressions` is
non-empty, the report says so in the same plain voice as everything else — do not soften it,
and do not lead with the successes to cushion it.

## Output contract

Write exactly one file: `artifacts/report.md`. Produce no other file and no other output.
