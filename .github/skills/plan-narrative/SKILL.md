---
name: plan-narrative
description: Write artifacts/plan-summary.md, the human-readable summary a reviewer reads before approving a pfandwerk run at the gate. Manual-only — run when the user invokes /plan-narrative after `synthgen patch plan` has produced artifacts/plan.json.
disable-model-invocation: true
---

# /plan-narrative

Read `AGENTS.md` first — its hard rules bind everything below.

Then follow `prompts/plan.md` exactly. Its source contract (`artifacts/plan.json` plus
verbatim `reason` text from `rules/gaps.yaml`, nothing else) and its output contract
(exactly one file, `artifacts/plan-summary.md`) bind.

This step belongs after PLAN and before the GATE. Never run `synthgen patch approve`,
`apply`, or `revert` — the gate belongs to the human.
