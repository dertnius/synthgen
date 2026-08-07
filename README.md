# SynthGen — synthetic test data generator for SQL Server

A .NET 8 CLI that turns a **CREATE TABLE DDL** plus a **YAML rules file** into bulk-loaded
synthetic test data, then **validates** the result with queries defined next to the rules.
Built for SQL Server 2019+ (parses with the official T-SQL 2019 grammar), designed to run
non-interactively so it can back a skill in an agentic workflow.

```
DDL (.sql) ─┐
            ├─> parse (ScriptDom) ─> plan ─> generate (Bogus, seeded) ─> SqlBulkCopy ─> evaluate (Dapper) ─> report + exit code
rules.yaml ─┘
```

## Quick start

```bash
# 1. Scaffold a rules file from your DDL (strategies inferred per column)
synthgen init --ddl samples/customers.sql --table dbo.Customers

# 2. Preview without touching a database
synthgen generate --ddl samples/customers.sql --rules Customers.rules.yaml --dry-run

# 3. Load + validate (connection via option or SYNTHGEN_CONNECTION env var)
synthgen generate --ddl samples/customers.sql --rules Customers.rules.yaml \
  --connection "Server=.;Database=Test;Integrated Security=true;TrustServerCertificate=true"

# 4. Re-run just the validations any time
synthgen evaluate --rules Customers.rules.yaml --json
```

No SQL Server handy? The whole loop also runs against SQLite for local smoke tests
(`--provider sqlite --create-table --connection local.db`) using a conda/micromamba-provided
SQLite — see [TESTING.md](TESTING.md) and `scripts/setup-sqlite.ps1`.

On a restricted network (nuget.org/anaconda.org blocked)? See
[ENTERPRISE-SETUP.md](ENTERPRISE-SETUP.md): internal-mirror config templates, an offline
NuGet feed exporter, and conda offline mode — everything generates locally, nothing is
downloaded at runtime.

During development, replace `synthgen` with `dotnet run --project src/SynthGen.Cli --`.
The sample schema has an FK chain: generate `samples/countries.rules.yaml` first, then
`samples/customers.rules.yaml` (its `CountryCode` column queries real values from Countries).

## Commands

| Command | Purpose |
|---|---|
| `init` | Parse DDL, emit a commented starter rules YAML (strategies, null rates, FK lookup queries, evaluation stubs all inferred from the DDL). |
| `generate` | Generate rows and bulk-load them; runs the rules file's evaluations afterwards unless `--no-evaluate`. `--dry-run` prints a sample; `--csv` writes a file instead of the DB. |
| `evaluate` | Run only the evaluations from a rules file. |

Common options: `--ddl`, `--rules`, `--table` (when the script has several tables),
`--connection` (falls back to `SYNTHGEN_CONNECTION`), `--rows`/`--seed` (override the rules
file), `--truncate`, `--json`, `--provider sqlserver|sqlite` (plus `--create-table` to
materialize the DDL when using SQLite locally).

### Exit codes (stable, for scripting)

| Code | Meaning |
|---|---|
| 0 | Success, all evaluations passed |
| 1 | Data loaded but at least one evaluation failed |
| 2 | Config error: bad DDL, rules file, or options |
| 3 | Database / runtime error |

## Rules YAML

```yaml
table: dbo.Customers        # optional if the DDL has a single table
rows: 1000
seed: 20260807              # omit for a random run; printed either way for reproducibility
truncateBeforeLoad: false   # TRUNCATE (DELETE fallback) before loading
batchSize: 10000            # SqlBulkCopy batch size

columns:
  CustomerId: { strategy: skip }                # IDENTITY — DB generates
  Email:      { strategy: template, template: "user{row}@example.test", unique: true }
  Age:        { strategy: int, min: 18, max: 90, nullRate: 0.1 }
  Status:     { strategy: pick, values: [Active, Inactive], weights: [0.8, 0.2] }
  CountryCode: { strategy: query, query: "SELECT CountryCode FROM dbo.Countries" }

evaluations:
  - name: row-count
    query: SELECT COUNT(*) FROM dbo.Customers
    expect: { equals: 1000 }
  - name: active-share
    query: SELECT CAST(SUM(IIF(Status='Active',1.0,0.0))/COUNT(*) AS FLOAT) FROM dbo.Customers
    expect: { between: ['0.75', '0.85'] }
  - name: avg-age            # no expect -> informational, value is just reported
    query: SELECT AVG(Age) FROM dbo.Customers
```

Any column **not** listed gets a rule inferred from the DDL (type, length, nullability,
identity, FK, unique). A listed column without a `strategy` also uses inference but keeps
your modifiers (`nullRate`, `unique`, `min`, `max`, `length`).

### Strategies

| Strategy | Produces | Options |
|---|---|---|
| `skip` / `dbDefault` | nothing — column excluded from the insert (identity, computed, DEFAULT) | |
| `int` | integers | `min`, `max` |
| `decimal` | decimals, rounded to the column scale | `min`, `max` |
| `bool` | bits | `trueRate` |
| `date` / `datetime` | dates (midnight) / timestamps | `min`, `max` |
| `time` | time of day | `min`, `max` |
| `guid` | seeded, reproducible v4-shaped GUIDs | |
| `string` | random alphanumeric | `length` |
| `bytes` | random binary | `length` |
| `template` | string with `{row}`, `{guid}`, `{rand:lo-hi}` tokens | `template` |
| `pick` | one of a fixed list, optionally weighted | `values`, `weights` |
| `sequence` | `start + step * (row-1)` | `start`, `step` |
| `faker` | realistic values via Bogus (`name.firstName`, `internet.email`, `address.city`, …) | `method` |
| `query` | random pick from a SQL result (FK integrity) — fetched once via Dapper | `query` |
| `constant` | fixed value | `value` |

Modifiers on any strategy: `nullRate` (0–1, nullable columns only) and `unique`
(duplicate-rejecting with a bounded retry and an actionable error).

### Expectations

`expect` compares the query's scalar result: `equals` (numeric when possible, with optional
`tolerance`; string fallback), `min` / `max`, or `between: [lo, hi]` — all inclusive.
No `expect` means informational: the value is reported, never fails the run.

## Agentic / skill usage

- Fully non-interactive; connection can come from the `SYNTHGEN_CONNECTION` env var so
  prompts never contain credentials.
- `--json` emits one machine-readable report on stdout (rows loaded, seed, warnings,
  truncations, per-evaluation pass/fail); progress and warnings go to stderr.
- Exit codes distinguish "data bad" (1) from "input bad" (2) from "infra bad" (3).
- One table per run, by design. For FK graphs, run dependency-first (Countries before
  Customers above) — the `query` strategy then samples real parent keys.
- Deterministic: same DDL + rules + seed ⇒ byte-identical data, so evaluations are stable.

## Performance

- Loading uses `SqlBulkCopy` (`TableLock`, `KeepNulls`, optional `KeepIdentity`) fed by
  batched, reused `DataTable`s — rows stream from the generator, memory stays flat at
  `batchSize` rows regardless of total count.
- Dapper is used where SQL results matter: `query`-strategy lookups (fetched once per run)
  and evaluations.
- Generation is allocation-light: one generator chain per column, values coerced straight
  into the bulk buffer.

## Layout

```
src/SynthGen.Core/     Ddl/ (ScriptDom parser)  Rules/ (YAML + inference + scaffolder)
                       Generation/ (plan, strategies, row generator)
                       Load/ (SqlBulkCopy, CSV)  Eval/ (Dapper evaluator)
src/SynthGen.Cli/      Spectre.Console.Cli commands: init, generate, evaluate
tests/SynthGen.Tests/  42 offline unit tests (parse, rules, generation, expectations)
samples/               customers.sql + countries/customers rules
```

Build & test: `dotnet build` / `dotnet test`. Pack as a tool: the CLI is a plain console
app — `dotnet publish -c Release` and put `synthgen` on PATH.

## Known limits (deliberate for the first cut)

- CHECK constraints are parsed and surfaced as comments in `init`, not enforced by the
  generator — encode them in rules, verify with evaluations (see the sample).
- Multi-column unique constraints aren't jointly enforced (single-column `unique` is).
- One table per run; orchestrate FK graphs by ordering runs.
- `query`-strategy value sets are held in memory (fine for lookup/parent tables).
