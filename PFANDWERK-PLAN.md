# pfandwerk — build plan

Rule-driven synthetic gap repair for SQL Server. Deterministic C# patches bad table data
(SynthGen.Core + Bogus). GitHub Copilot CLI agents run **locally only**, for planning narrative and
report prose. GitLab CI is a notary: it audits artifacts, it never generates.

pfandwerk is a **subsystem of this repository**, not a separate tree. Where SynthGen bulk-generates
rows into empty tables, pfandwerk patches bad or empty values in rows that already exist. It reuses
`SynthGen.Core` for the generator whitelist, YAML loading, expectation checking, and T-SQL parsing
(§4).

**Dispatch note for the coding agent:** execute phases in order (§7). Stop at every phase gate and
report. Do not re-decide anything in §2 — those decisions are final. Never violate §9.

---

## 1. Context

- Target: on-prem SQL Server dev/test databases where upstream interfaces write bad or empty values
  (example table: `dbo.Property`).
- Consumers of the patched data: automated tests / CI only. Tests define quality; no realism or
  distribution work.
- Stack: .NET 10 / C#, PowerShell 7, Bogus, xUnit, Dapper, ScriptDom, GitLab CI, GitHub Copilot CLI
  (local), Data API Builder. DAB is a supported write path behind `IPatchSink`; `SqlPatchSink`
  is the default because only it can span the ledger and target writes in one transaction (D12).
- Constraint: Copilot CLI is not allowed to run in the GitLab pipeline. The whole agentic loop runs
  on the developer machine.
- Constraint: the build must work on a restricted network. The fixture end-to-end runs offline on
  SQLite via `src/SynthGen.Sqlite`, the same way this repo's existing tests do.

## 2. Decision record (locked)

| # | Decision |
|---|---|
| D1 | Consumer = CI only → quality bar is the test suite, nothing more |
| D2 | Identity fields (e.g. SecurityId): generated once, frozen forever. Ephemeral fields: generated fresh **at PLAN time**, frozen into `plan.json`, and never re-randomized on a later run — once a value is patched the gap predicate no longer matches it |
| D3 | Identity ledger = separate config database. Ledger write and target write commit **in one transaction**. Collision check at plan time |
| D4 | Human gate = two-tier review (tier 1: every new identity; tier 2: counts + 5 samples + per-rule threshold) with SHA-256-bound approval. The hash covers the concrete values that will be written |
| D5 | VERIFY = re-scan + invariant tests + consumer CI suite, diffed against a baseline captured at scan time by the **same** test-runner code path |
| D6 | Failure routing: L1 (gaps remain) → one retry, then stop. L2/L3 regression → hard stop, human decides. No blanket retries |
| D7 | Revert = manual local tool only, replays `patches.jsonl` new→old through the same write path. Ledger rows are never deleted. Never a CI job |
| D8 | Report = Copilot maker agent writes the narrative |
| D9 | Report audit is **deterministic C#** (`ReportAuditor`) against `facts.json`, run identically in the local loop and in CI. No LLM checker, no reject loop |
| D10 | Environment guard = code-side allowlist matched on Server + Database + auth mode, canonically compared; abort on mismatch. Allowlist entries never contain passwords |
| D11 | Rules change only via reviewed MR. The plan approver is never the rule author — the notary enforces this, it is not left to convention |
| D12 | Write path is `IPatchSink`, selected with `--sink sql\|dab`. `SqlPatchSink` (Microsoft.Data.SqlClient, transactional) is the default; `DabPatchSink` is the API-layer path for sites that mandate one. Both are built and tested; `dab/dab-config.json` is generated and `dab validate`-clean |
| D13 | Threat model is **accident and drift, not a malicious insider**. The notary verifies internal consistency of a file set one actor produced; it cannot catch coordinated edits to `patches.jsonl` and `facts.json` |
| D14 | One concurrent run per target database. A ledger PK/UQ violation aborts with a named exit code and an actionable message |
| D15 | Local-only Copilot. GitLab CI runs exactly one job: a deterministic artifact audit (notary) |
| D16 | Third rule kind `derived`: value is a pure function of other columns in the same row, declared in `inputs`. Separate whitelist `Func<IReadOnlyDictionary<string, object?>, object>` — the ephemeral signature is never widened. Carries `onMissingInput` (block \| floor, default block) and an optional full-table `invariant` |
| D17 | VERIFY layer 1 compares against the **planned** row set, not the raw gap-predicate count. A row deliberately skipped is not a failure to close a gap |
| D18 | Any rule carrying an `invariant` is cross-checked against it at PLAN time and the result printed at the gate: rows it rejects that the gap misses, rows the gap selects that it accepts, and rows it cannot evaluate. All three are **advisory** — printed and recorded, never blocking. VERIFY layer 2 additionally reports its unevaluated-row count, because `NOT (invariant)` passes those rows in silence |

### Why D2, D3, D9 and D12 read this way

These four amend an earlier draft; the reasons are load-bearing, so they are recorded rather than
left implicit.

- **D2** — with values generated at apply time, `sha256(plan.json)` bound samples that were merely
  illustrative. The tier-2 human review and the hash-binding were both decorative. Generating at
  plan time makes the approved artifact the executed artifact and makes APPLY a pure executor that
  calls no generator.
- **D3** — an append-only ledger written before a separate, non-transactional patch cannot express
  "reserved but not applied". A failed patch left a row that `UQ_Ledger_Value` locks forever,
  invisible to `patches.jsonl`, Revert and FactExtractor. One transaction removes the state class
  entirely. On `DabPatchSink`, where a spanning transaction is impossible, a ledger row
  means **reserved**, applied-state derives from `patches.jsonl`, and Planner treats a ledger hit as
  "reuse this value" without assuming the database holds it.
- **D9** — §6.11 already required this check in C# for CI. An LLM checker doing mechanical
  number-matching duplicated code that had to exist anyway, and the LLM copy is the one that can be
  wrong.
- **D12** — DAB means per-row HTTP, a live `dab start` during both APPLY and Revert, and no
  transaction able to span the ledger write. It also breaks the offline fixture story. Behind an
  interface it stays available to sites that need it without being load-bearing for everyone.
- **D16** — `Func<Faker, object>` has no access to the row, so no whitelist entry could ever produce
  a value derived from a sibling column. The two maps stay separate rather than widening the
  ephemeral signature: an ephemeral generator able to read the row would become silently
  order-dependent, and its output would stop being reproducible from `plan.json` alone — the exact
  property D2 exists to protect. Worked end-to-end in
  `docs/example-security-bathrooms.md`.
- **D17** — followed directly from the example. A derived rule whose `inputs` are unusable skips the
  row rather than guessing; that row is still a gap afterwards, and the original layer-1 wording
  ("planned rules must be 0 gaps") would have failed a correct run.
- **D18** — every other check validates a rule's *shape*: it parses, its columns exist, its generator
  resolves, it stays under its threshold. `SignedDate IS NULL` and `SignedDate IS NULL AND Status =
  'active'` pass all of them and mean different things. The invariant is the only other statement of
  intent in the rule, so comparing the two is the only available check on whether the predicate
  selects what a person meant.

  Building it surfaced a hole in a shipped check, which is now the more important half. Layer 2's
  query is `NOT (invariant)`; under three-valued logic a row where the invariant is UNKNOWN — a NULL
  in any column it references — is neither a violation nor a pass, and disappears. On a six-row
  fixture: `TRUE 2 · FALSE 3 · INDETERMINATE 1`, with the indeterminate row absent from the reported
  violations. Layer 2 has been reporting green on rows it never judged. The counterpart on the query
  side is that "not selected by the gap" must be `key NOT IN (SELECT key … WHERE gap)` rather than
  `NOT (gap)`, for exactly the same reason — pinned by test, because the naive form looks obviously
  equivalent.

  Advisory rather than blocking: `SEC-001`'s row 104 has an unusable input, so no rule can repair it,
  and a run that refused to proceed would be wrong. Making indeterminate rows fail layer 2 is
  arguably more honest and is deliberately deferred — it would newly fail runs that pass today the
  moment a NULL appears in a referenced column, and the fix is to rewrite the invariant, not the
  data. Surface it first, see how often it fires.

## 3. Architecture

```
LOCAL (run.ps1 = dumb spine, PowerShell)
  1 PLAN    pfandwerk plan         guard + scan + plan, values frozen
                                   → plan.json, baseline.json
            copilot -p plan.md     → plan-summary.md (narrative only)
  2 GATE    pfandwerk approve      → plan.approved {sha256, user, ts}
  3 APPLY   pfandwerk apply        ledger+patch in one tx → patches.jsonl
  4 VERIFY  pfandwerk verify       exit 0 | 10 | 20 | 30
  5 REPORT  copilot -p report-maker.md → report.md
            pfandwerk report       facts → audit → fallback
                                   → facts.json, report.audit.json
REMOTE (GitLab CI)
  NOTARY    pfandwerk notary       recompute + cross-check committed artifacts
```

Agents never write data. Every database write path is deterministic C#.

## 4. Reuse — what pfandwerk does not rebuild

Everything below exists in this repository and is under test.

| Need | Existing code |
|---|---|
| Generator whitelist — "the ONLY value source" | `src/SynthGen.Core/Generation/FakerMap.cs` — already `Dictionary<string, Func<Faker, object>>`, curated |
| Typed value generators (int/decimal/date/template/pick) | `src/SynthGen.Core/Generation/ValueGenerators.cs` |
| YAML load + schema validation with position info | `src/SynthGen.Core/Rules/RulesLoader.cs` |
| Invariant assertions with thresholds | `src/SynthGen.Core/Eval/Evaluator.cs` — `Check(...)` is static and pure; the `Func<IDbConnection>` constructor makes it provider-agnostic |
| T-SQL parsing for gap-predicate validation | `src/SynthGen.Core/Ddl/DdlParser.cs` (ScriptDom) |
| Provider-abstraction precedent | `src/SynthGen.Core/Load/ITableLoader.cs` — `IPatchSink` mirrors its shape |
| Offline fixture database, skip-not-fail tests | `src/SynthGen.Sqlite/`, `tests/SynthGen.Tests/Support/SqliteFactAttribute.cs` |
| Exit codes, `--json` output, connection env var | `src/SynthGen.Cli/CliSupport.cs` |
| Restricted-network build | `scripts/setup-enterprise.ps1`, `enterprise-profile.psd1` |

## 5. Repo layout

One project in `SynthGen.sln` plus its tests. Tools are **verbs on one CLI** — a single
parsed invocation is what lets the agent permission layer allow exactly `pfandwerk <verb>`.

```
src/Pfandwerk/              one project, seven files
├─ Program.cs               args, dispatch, the verbs' console output
├─ Rules.cs                 GapRule, loader, predicate validator, GapQuery
├─ Generators.cs            the two value whitelists
├─ Data.cs                  DbContext, ledger, allowlist, artifact records, helpers
├─ Run.cs                   plan (scan + plan in one pass), apply, verify
└─ Report.cs                FactExtractor, ReportAuditor, ArtifactAuditor, Reverter
                            verbs: plan approve apply verify report notary revert generators
tests/Pfandwerk.Tests/      unit + SQLite-backed end-to-end (SqliteFact on DB-touching tests)
AGENTS.md                   hard rules only — stable prompt-cache prefix
allowlist.json              permitted targets (dev/test only, no passwords)
rules/gaps.yaml · rules/consumer-checks.yaml
prompts/plan.md · prompts/report-maker.md
db/pfandwerk/ledger.sql · ledger.sqlite.sql   embedded, so code cannot drift from them
db/pfandwerk/fixtures/security.sql · security-broken-seed.sql
dab/dab-config.json · dab/README.md    DAB config (generated, dab validate-clean)
run.ps1                     the spine
artifacts/                  gitignored except committed run outputs
.gitlab-ci.yml
```

**Three things the plan named that were deliberately not built.** `hooks/pre-tool-use.ps1`
and `hooks/post-tool-use.ps1` were dropped when P0b found Copilot CLI has no hook mechanism
— its native `--available-tools` / `--deny-tool` / `--add-dir` flags do that job instead
(hard rule 6). `approve.ps1` and `run-report.ps1` became the `approve` and `report` verbs,
so the permission layer has one command shape to allow rather than several scripts.

### Exit codes

Extends `CliSupport.ExitCodes`, so the two CLIs never disagree.

| Code | Meaning |
|---|---|
| 0 | Success |
| 2 | Config error: bad rules, bad options, schema drift |
| 3 | Database / runtime error |
| 4 | Connection not in allowlist |
| 5 | Concurrent run / ledger conflict (D14) |
| 10 | VERIFY layer 1: planned gaps remain |
| 20 | VERIFY layer 2: invariant regression |
| 30 | VERIFY layer 3: consumer-suite regression |

## 6. Component specs

### 6.1 rules/gaps.yaml
Per rule: `id, table, key, column, kind (ephemeral|identity), gap (SQL predicate), fix (FakerMap
key), threshold (int), reason (string)`. `key` names the column(s) identifying a row; composite keys
are a list. Ship 3 example rules: PROP-001 EnergyClass (ephemeral), PROP-002 YearBuilt (ephemeral),
PROP-003 SecurityId (identity).
Acceptance: schema validated by `GapRulesLoader`; invalid file → exit 2 with position info, matching
`RulesLoadException`'s existing style.

### 6.2 GapPredicateValidator
Parses each `gap` as a boolean expression with ScriptDom. Rejects multiple statements or a statement
terminator; walks the AST and rejects any table reference outside the rule's declared `table`. This
is what makes hard rule 4 enforceable rather than aspirational — a subquery could otherwise reach
any table in the database.
Acceptance: unit tests for a batch-separator injection, a cross-table subquery, and a valid
multi-clause predicate.

### 6.3 GapQuery
The single code path turning a rule predicate into SQL. SCAN, PLAN and VERIFY layer 1 all call it,
so the three cannot drift. Emits both the `COUNT(*)` form and the key+value row form.
Acceptance: unit test asserting all three callers produce identical SQL for one rule.

### 6.4 AGENTS.md
Hard rules only (§9), no per-run data. Volatile content is appended by prompt files, never here.
Acceptance: under 60 lines; contains every rule from §9 verbatim.

### 6.5 Prompts
- `plan.md`: run `pfandwerk plan`, then write `artifacts/plan-summary.md` grouping by rule id, using
  only reason texts from rules and numbers from `plan.json`. No other output.
- `report-maker.md`: write `report.md` FROM `facts.json` only. Structure: header (run id, plan sha,
  rules sha, approver), per-rule section (count, why, 3 samples), new identities table, verify table
  including pre-existing reds vs regressions.

There is no checker prompt — D9 makes that deterministic.
Acceptance: each prompt ends with an explicit output-file contract line.

### 6.6 Scanner
Validates rules against the **live schema** first (a rule naming a dropped column is a config error,
exit 2, not a runtime failure). Runs gap counts through `GapQuery`. Captures the baseline via
`TestRunner` (§6.9).
Output: `artifacts/plan.json`, `artifacts/baseline.json`.
Acceptance: against the broken fixture seed, finds exactly the seeded gap counts.

### 6.7 Planner (deterministic)
For each gap: copy fix/reason/threshold from the rule.
*Identity columns* — query the ledger; hit → reuse; miss → generate from `FakerMap`, collision-check
against the target database **and** the ledger, add to `newIdentities` with the concrete value.
*Ephemeral columns* — generate the concrete value now and freeze it in `plan.json` (D2).
Every value is canonicalized to an invariant-culture string for ledger storage and comparison, so
decimals, dates and trailing zeros round-trip identically. Records `rulesSha` next to `planSha` so
the notary can prove which rules produced the plan. Count > threshold → `status: BLOCKED`.
Output: `artifacts/plan.json`. The hash is recomputed on demand; `plan.approved` records the approved one.
Acceptance: unit tests — identity reuse, collision regeneration, threshold block, canonical
round-trip, and a byte-identical plan from the same inputs.

### 6.8 approve.ps1 (the gate)
Prints tier 1 (full new-identities list) and tier 2 (per-rule count, the actual frozen samples,
threshold state). Refuses if any rule is BLOCKED. On explicit `yes`: writes
`plan.approved {sha256, osUser, gitEmail, timestampUtc}`.
Acceptance: tampering with `plan.json` after approval makes APPLY abort.

### 6.9 Patcher and TestRunner
Patcher recomputes `sha256(plan.json)` and compares to `plan.approved` — mismatch → abort, never
trusting script order. Per row, through `IPatchSink`: `SqlPatchSink` opens one transaction, INSERTs
the ledger row in the config database and UPDATEs the target, and commits both or neither.
Values come only from the frozen plan; APPLY calls no generator. `--on-row-error stop|continue`
(default `stop`) makes per-row failure handling explicit. Logs every change
`{ts, rule, id, col, old, new, reason}` → `artifacts/patches.jsonl`. Re-running patches only
remaining gaps.

`TestRunner` is one class invoked identically by SCAN (baseline) and VERIFY (after), so both halves
of the diff are produced the same way. Two independent implementations would guarantee phantom
regressions from test-id, filter and ordering differences.
Acceptance: unit tests — hash-mismatch abort; unknown fix key rejected; an injected sink failure
leaves neither a ledger row nor a patched value.

### 6.10 Verify
Layer 1 re-scan through `GapQuery` (planned rules must be 0 gaps) → exit 10. Layer 2 invariants via
`Evaluator`, layer 3 consumer suite; diff against `baseline.json`; any green→red regression → exit
20 / 30. Pre-existing reds are listed, never fail the run. Writes `artifacts/verify.json`.
Acceptance: fixture test with a pre-seeded red consumer test proves no false failure.

### 6.11 FactExtractor
From `patches.jsonl` + `verify.json` + `plan.approved` + rules: emit
`facts.json {runId, planSha, rulesSha, approver, ts, rules:[{id, patched, samples, reason}],
newIdentities:[], verify:{l1,l2,l3,regressions,preexistingReds}}`.
Acceptance: golden-file unit tests. This is the load-bearing component — test it hardest.

### 6.12 ReportAuditor
Extracts every number and rule id from `report.md`; asserts each appears in `facts.json`; asserts
every `facts.json` rule id appears in `report.md`. Emits
`report.audit.json {verdict: pass|fail, violations: []}`. Same class runs locally and in CI.
Acceptance: mutating one number in a fixture report yields `verdict: fail` naming that number.

### 6.13 run-report.ps1
Maker (`$env:PFANDWERK_MODEL_MAKER`) writes `report.md` → `pfandwerk report` → on `fail`,
re-run the maker once with the violations appended → on a second `fail`, fall back to rendering
`facts.json` as a bare markdown table with the note "narrative failed audit". Publish only audited
or fallback output; embed plan sha, rules sha and audit stamp.
Acceptance: a forced-fail path produces the fallback file.

### 6.14 ArtifactAuditor (CI notary)
Recomputes `sha256(plan.json)` vs `plan.approved`; re-runs FactExtractor over committed
`patches.jsonl` and diffs against committed `facts.json`; runs `ReportAuditor`; checks `rulesSha`
matches the committed rules; enforces four-eyes (D11) by failing when `plan.approved.gitEmail`
equals the author of the last commit touching `rules/gaps.yaml`; fails if trajectory logs are
absent.
Per D13 this catches drift and accident, not collusion.
Acceptance: mutating one count in `report.md` turns the job red; an approval by the rules author
turns it red.

### 6.15 Revert (local only)
Replays `patches.jsonl` backwards (new→old) through `IPatchSink`, per rule or per full run. Never
deletes ledger rows. Interactive confirm.
Acceptance: e2e — after revert the database equals the before-state, ledger row count is unchanged,
and re-apply reuses identical SecurityIds.

### 6.16 db/pfandwerk/ledger.sql
```sql
CREATE TABLE dbo.SyntheticLedger (
  TargetTable sysname NOT NULL,
  RowKey nvarchar(128) NOT NULL,
  ColumnName sysname NOT NULL,
  Value nvarchar(400) NOT NULL,
  RuleId varchar(32) NOT NULL,
  CreatedAt datetime2 NOT NULL DEFAULT sysutcdatetime(),
  CreatedBy nvarchar(128) NOT NULL,
  CONSTRAINT PK_SyntheticLedger PRIMARY KEY (TargetTable, RowKey, ColumnName),
  CONSTRAINT UQ_Ledger_Value UNIQUE (TargetTable, ColumnName, Value)
);
```
The unique constraint makes the database enforce collision safety a second time. Key widths are
within SQL Server's 1700-byte index limit (PK 768 B, UQ 1312 B).

### 6.17 ConnectionAllowlist
Parses target and candidate with `SqlConnectionStringBuilder` and compares Server + Database + auth
mode canonically — never raw string equality. Hard-rejects any allowlist entry containing
`Password=` or `Pwd=`, because `allowlist.json` is committed and a pasted credential would enter git
history permanently.
Acceptance: unit tests for a case/whitespace-variant match, a database mismatch, and a
password-bearing entry.

### 6.18 Hooks
`pre-tool-use.ps1`: allow only `pfandwerk <verb>` invocations, matched by parsing the command and
normalizing path separators — not by regexing the raw string, which breaks on Windows separators and
wrapper invocations. Deny writes outside `artifacts/`.
`post-tool-use.ps1`: append every tool call to `artifacts/trajectory/<phase>.jsonl`.
The enforcement mechanism is confirmed by the P0 spike (§7) before this is built.
Acceptance: a blocked-command test and a trajectory-line test.

### 6.19 Fixtures
`dbo.Property` schema + broken seed (NULLs, `0`, `'X9'`). The fixture builds on SQLite through
`SqliteSchemaBuilder` for offline runs, and on `mcr.microsoft.com/mssql/server:2022-latest` via
`docker-compose.yml` when the network allows. `DabPatchSink` is exercised only in the container
configuration.

### 6.20 .gitlab-ci.yml
One stage, one job: `notary` running `pfandwerk notary` on committed artifacts. No database access
required.

## 7. Build phases (dispatch order)

| Phase | Build | Gate (stop + report) |
|---|---|---|
| P0 | **Copilot CLI spike** (timeboxed, half a day): confirm pre/post-tool-use interception and deny semantics, and the `-p` non-interactive output contract → `docs/copilot-cli-findings.md` | Findings recorded and hard rule 6's enforcement chosen — hooks if supported, otherwise container isolation with a read-only mount and no DB route. If the latter, §3 changes before P1 proceeds |
| P1 | Contracts: `rules/gaps.yaml` + `GapRulesLoader`, `allowlist.json`, `AGENTS.md`, 2 prompts, `.gitignore` | YAML parses; `AGENTS.md` under 60 lines and contains §9 verbatim; no committed connection string carries a password |
| P2 | `db/pfandwerk/`: ledger DDL, fixtures, SQLite builder, docker-compose | Fixture builds offline on SQLite **and** on the container when reachable; seeded gap counts are known constants |
| P3a | Guard, `GapPredicateValidator`, `GapQuery`, Scanner, Planner + tests | `dotnet test` green: identity reuse, collision regeneration, threshold block, predicate rejection, schema-drift rejection, exact seeded counts |
| P3b | `IPatchSink`, `SqlPatchSink`, `DabPatchSink`, Patcher, `TestRunner`, Verify + tests | `dotnet test` green: hash-mismatch abort, unknown fix key rejected, injected sink failure rolls back both writes, pre-existing red reported as pre-existing |
| P3c | FactExtractor, `ReportAuditor`, Reverter + tests | `dotnet test` green; a mutated fixture report yields `verdict: fail` |
| P4 | Spine: `run.ps1`, `approve.ps1`, `run-report.ps1`, hooks per P0 | `run.ps1 -WhatIf` walks every phase with no DB writes; blocked-command and trajectory tests pass |
| P5 | End-to-end on the fixture database | Definition of done (§8) |
| P6 | `.gitlab-ci.yml`, README runbook | CI audit green on committed run artifacts; mutation turns it red |

## 8. Definition of done (e2e)

Each step is asserted individually with its own evidence, not as one prose chain.

1. Clean fixture, broken data seeded — gap counts match the seed exactly.
2. `run.ps1` reaches the gate; the gate shows 2 ephemeral rules with their **frozen** values and 1
   identity list.
3. Approve → APPLY → `patches.jsonl` has one line per planned change, and every written value equals
   the value in the approved `plan.json`.
4. Re-scan reports 0 gaps on planned rules.
5. Verify exits 0, with the pre-seeded red consumer test listed as pre-existing.
6. `facts.json` matches its golden shape; `report.md` publishes with `audit=pass`.
7. Artifacts committed; CI audit green; mutating one count turns it red.
8. `Revert` restores the before-state; ledger row count unchanged.
9. A second full run reuses the identical SecurityIds.

## 9. Hard rules (never violate)

1. No agent ever writes to a database or generates a data value. All writes go through `IPatchSink`;
   all values come from the `FakerMap` whitelist, frozen into `plan.json` at PLAN time.
2. Patcher independently verifies `sha256(plan.json)` against `plan.approved`.
3. Ledger and target writes commit atomically on the default sink. Ledger rows are never updated or
   deleted.
4. Only tables listed in `rules/gaps.yaml` may be touched — enforced by AST inspection of every gap
   predicate, not by convention.
5. Connection strings must match `allowlist.json` on Server + Database + auth mode, or the run
   aborts. Allowlist entries never contain passwords.
6. Copilot CLI runs locally only; agent tool access is denied by default via the CLI's own
   permission system — `--available-tools` for visibility, `--deny-tool` for exceptions,
   `--add-dir` for filesystem scope, `--deny-url` for network. Not hooks: the CLI has none.
7. Revert is never wired into CI.
8. `report.md` ships only with `audit=pass` from `ReportAuditor`, or as the bare-facts fallback.
9. Rules and the generator whitelist change only via reviewed MR, and the plan approver is never the
   rules author — enforced by the notary.

## 10. Out of scope

Auto-revert, realism/distribution tuning, production databases, scheduling or orchestration beyond
`run.ps1`, multi-writer concurrency (D14), and defence against a malicious insider (D13).

**Cross-column coherence was on this list and has been removed** — see D16. It was excluded on the
assumption that every repaired value would be independently generated; the `dbo.Security` example
(one bathroom per three rooms, minimum one) is a value derived from another column of the same row,
so the exclusion no longer held.
