---
name: draft-rules
description: Draft gap-rule proposals from artifacts/survey.json into rules/drafts/ for a person to correct. Use after `synthgen patch survey` when asked to draft, propose, or scaffold repair rules for surveyed tables.
argument-hint: "[tables to focus on, optional]"
---

# /draft-rules

Read `AGENTS.md` first — its hard rules bind everything below.

Then follow `prompts/draft-rules.md` exactly. Its source contract (`artifacts/survey.json`
and nothing else), its skip rules, and its output contract (one YAML per table into
`rules/drafts/`, nothing else) bind.

Never write to `rules/gaps.yaml`, and never run `synthgen patch plan`, `approve`,
`apply`, or `revert`.
