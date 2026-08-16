---
name: synthgen
description: Generate and validate synthetic SQL Server test data with the synthgen CLI. Use when asked to create test data from a CREATE TABLE DDL, scaffold or edit a rules YAML, bulk-load rows, run or re-run evaluations, or work offline against SQLite.
---

# synthgen — generating test data

The full command, option, rules-YAML, and exit-code reference is `README.md`. Do not
restate it — read it. The short shape:

1. `synthgen init --ddl <file.sql> --table <schema.Table>` scaffolds a commented rules
   YAML with strategies inferred per column.
2. `synthgen generate --ddl <file.sql> --rules <table>.rules.yaml` generates and
   bulk-loads, then runs the file's evaluations. `--dry-run` previews without a database.
3. `synthgen evaluate --rules <table>.rules.yaml --json` re-runs just the validations.

During development, `synthgen` means `dotnet run --project src/SynthGen.Cli --`.

Facts that prevent wasted runs:

- Connection comes from `--connection` or the `SYNTHGEN_CONNECTION` env var — never put
  credentials in a prompt or a committed file.
- One table per run. For FK graphs, run dependency-first; the `query` strategy samples
  real parent keys (see `samples/`).
- No SQL Server available: the whole loop runs on SQLite —
  `--provider sqlite --create-table --connection local.db` — see `TESTING.md`.
- Exit codes are stable for scripting: 0 ok, 2 bad input, 3 infra, 40 data loaded but an
  evaluation failed. Full table in `README.md`.
- Repairing *existing* rows is not this skill's job — that is the pfandwerk-repair skill.
