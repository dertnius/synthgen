---
name: pfandwerk-repair
description: Repair bad values in existing rows behind a human approval gate (the pfandwerk subsystem, synthgen patch). Use when asked about surveying data quality, writing gap rules, patching or repairing existing rows, the run phases, run artifacts, or patch exit codes.
---

# pfandwerk — repairing existing rows

Read `AGENTS.md` first — its hard rules bind every agent in this repository. The
walkthrough with real output is `docs/runbook.md`; do not restate it — read it.

The run is a fixed walk, sequenced by `run.ps1`:

GUARD → SCAN → PLAN → **GATE** → APPLY → VERIFY → REPORT

Who does what — this boundary is the whole design:

- **Deterministic C#** decides every row and generates every value (frozen into
  `artifacts/plan.json` at PLAN, approval SHA-bound).
- **A human** approves at the GATE, edits drafts into `rules/gaps.yaml`, and runs
  `revert`. Agents never do these. Never run `synthgen patch approve`, `apply`, or
  `revert`, and never run `run.ps1` — it contains the gate.
- **Agents** write prose only: rule drafts (draft-rules skill), the plan summary
  (plan-narrative skill), the run report (report-maker skill).

What an agent may run: `synthgen patch survey` (read-only profiling), `synthgen patch
generators` (list valid fix keys), reads under `artifacts/` and `rules/`, and — only when
a task prompt asks for it — `plan`, `verify`, or `report` (they compute and write
artifacts, never data).

Outcome reading: exit 0 clean · 10 gaps remain · 20 invariant regression · 30 consumer
regression · 4 connection not allowlisted · 5 ledger conflict. Regressions and
pre-existing failures are different things and are never merged — `docs/runbook.md` §5.
