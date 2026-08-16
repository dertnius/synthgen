---
name: report-maker
description: Write artifacts/report.md, the record of what a pfandwerk run changed, from artifacts/facts.json. Manual-only — run when the user invokes /report-maker after VERIFY has produced artifacts/facts.json. A deterministic auditor re-checks every figure.
disable-model-invocation: true
---

# /report-maker

Read `AGENTS.md` first — its hard rules bind everything below.

Then follow `prompts/report-maker.md` exactly. Its source contract (`artifacts/facts.json`
and nothing else) and its output contract (exactly one file, `artifacts/report.md`) bind.
`ReportAuditor` — deterministic C# — rejects the file if any figure or rule id diverges
from `facts.json`, so anything you cannot source, omit.

After writing, the deterministic step `synthgen patch report` audits and ships it; do not
run anything else.
