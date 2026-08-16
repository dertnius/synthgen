# Who does what — tools, gap detection, and how the agents are called

The design history ([docs/history/PFANDWERK-PLAN.md](history/PFANDWERK-PLAN.md) §3) gives
the phase order. This file answers the next question: which tool is actually running at
each point, what it is allowed to touch, and where the agents sit relative to the
database.

The single organising rule: **agents produce prose, code produces data.** Everything below
is a consequence of that.

## 1. The tools, and what each is for

| Tool | Role | Runs during | Touches the DB? |
|---|---|---|---|
| **Microsoft.Data.SqlClient** | every read and every write to SQL Server | SCAN, PLAN, APPLY, VERIFY, REVERT | yes — this *is* the database access |
| **Dapper** | thin mapping over SqlClient for scalar/row queries | SCAN, PLAN, VERIFY | yes |
| **ScriptDom** | parses each rule's `gap` predicate into an AST; rejects multi-statement or cross-table predicates | rule load (start of every phase) | no — pure parsing |
| **YamlDotNet** | loads `rules/gaps.yaml` | rule load | no |
| **Bogus** (`Faker`) | produces values for `ephemeral` and `identity` columns | **PLAN only** | no — generates in memory |
| **xUnit** | invariant suite + consumer suite, both halves of the baseline diff | SCAN (baseline), VERIFY | yes, via the suites' own connections |
| **GitHub Copilot CLI** | writes two narrative documents | PLAN (summary), REPORT | **no — denied by default** |
| **Data API Builder (DAB)** | alternative write path, `--sink dab` | APPLY, REVERT — only if selected | yes, if used |

Two of these deserve immediate qualification.

**DAB is implemented and selectable with `--sink dab`.** `SqlPatchSink` remains the
default because only it can enrol the ledger write and the target write in one
transaction (D12); on the DAB path a ledger row means *reserved* rather than *applied*.
Configuration, per-setting rationale, limits, and the regeneration recipe live in
[`dab/README.md`](../dab/README.md) — the sole home for DAB facts.

**Bogus runs at PLAN, never at APPLY.** This is the D2 decision made concrete. By the time
`Patcher` runs, every value already exists as a literal inside `plan.json`, and that file's
SHA-256 is what the human approved. `Patcher` contains no generator call at all — it is a
pure executor.

## 2. Who detects the gaps

**Deterministic C#. No agent, no Bogus, no DAB, no LLM.**

A rule declares its gap as a SQL boolean predicate:

```yaml
- id: SEC-001
  table: dbo.Security
  column: Bathrooms
  gap: "PropertyType = 'EFH' AND Bathrooms IS NULL"
```

Detection is three steps.

**Step 1 — parse and constrain (ScriptDom).** Before the predicate is ever sent to a
database, `GapPredicateValidator` parses it as a boolean expression and rejects it if it
contains a statement terminator, more than one statement, or a reference to any table other
than the rule's declared `table`. That last check is what makes hard rule 4 — *only tables
listed in `rules/gaps.yaml` may be touched* — enforceable rather than aspirational: without
it, a subquery inside a predicate could read anything in the instance.

The rule file is trusted-but-reviewed input (hard rule 9: changes only via reviewed MR), so
this is defence in depth rather than the only line of defence.

**Step 2 — build the SQL (`GapQuery`).** One class turns a predicate into SQL, in exactly
two shapes:

```sql
-- count, for thresholds and reporting
SELECT COUNT(*) FROM dbo.Security WHERE PropertyType = 'EFH' AND Bathrooms IS NULL;

-- rows, with the key, the current value, and any declared inputs
SELECT PropertyId, Bathrooms, Rooms FROM dbo.Security
WHERE PropertyType = 'EFH' AND Bathrooms IS NULL;
```

`GapQuery` being a single class is not tidiness. The same predicate is evaluated at three
separate moments — SCAN, PLAN, and VERIFY layer 1 — and if those three built their SQL
independently they would eventually disagree about what a gap is, which would show up as a
run that reports success while leaving gaps behind.

**Step 3 — execute (Dapper over SqlClient).** `Planner` runs the queries and writes
`artifacts/plan.json`. That file, not the database, is what every later phase reads.

Nothing about this step involves a model. An agent could not detect a gap if it wanted to:
the CLI permission flags deny it database access, and the only tool it is permitted to run is
`synthgen patch <verb>`.

## 3. Who produces the values

Three kinds, three different sources, no overlap.

| Kind | Source | Signature | Ledger | Example |
|---|---|---|---|---|
| `ephemeral` | Bogus via `FakerMap` | `Func<Faker, object>` | no | `EnergyClass` |
| `identity` | Bogus via `FakerMap`, then collision-checked against the live table **and** the ledger | `Func<Faker, object>` | **yes, permanent** | `SecurityId` |
| `derived` | pure function of the row's declared `inputs` | `Func<IReadOnlyDictionary<string, object?>, object>` | no | `Bathrooms` from `Rooms` |

`FakerMap` already exists — `src/SynthGen.Core/Generation/FakerMap.cs` is literally the
`Dictionary<string, Func<Faker, object>>` the plan calls for, and `FakerMap.Resolve` throws
on an unknown key with the list of valid ones. pfandwerk composes it and adds domain
entries; a rule naming a key that is not in the whitelist fails at load, which is hard rule
1 enforced by the type system rather than by review.

Derived values deliberately get a **separate** map. `Func<Faker, object>` cannot see the
row, so it could never compute bathrooms from rooms — and widening it to pass the row would
make every ephemeral generator potentially order-dependent, destroying the reproducibility
that freezing values at PLAN time exists to guarantee.

All three run inside `Planner`. All three land as literals in `plan.json`. The SHA-256 the
reviewer approves covers all of them.

## 4. Who writes to the database

One interface, mirroring the `ITableLoader` pattern this repo already uses for SynthGen's
bulk loading:

```csharp
public interface IPatchSink
{
    /// Applies the write and returns the value the column held beforehand.
    string? Apply(PatchInstruction instruction);
}
```

- **`SqlPatchSink`** (default) — opens one transaction, INSERTs the ledger row when the
  rule is an identity, UPDATEs the target row, commits both or neither. This is why
  `patch apply` requires the ledger and the target to be the same database — a second
  database cannot join that transaction, and the run refuses rather than degrade to a
  non-atomic reservation.
- **`DabPatchSink`** (`--sink dab`) — GET then PATCH against a running `dab start`. No
  transaction can span the ledger write there, so a ledger row means *reserved* and
  applied-state derives from `patches.jsonl`. A non-2xx aborts; it never falls back to SQL.
- **A fake** — injected in tests to fail after the ledger write, proving the rollback
  leaves neither a ledger row nor a patched value. This is the same technique as
  `tests/SynthGen.Tests/Support/FakeTableLoader.cs`: the seam is one method, so a fake beats
  a mocking framework.

`Revert` uses the same interface, so there is one write path and one set of tests for it.

## 5. How the agents are called

Two agents in the run itself. Both are `copilot -p <prompt file>`, both write exactly one
markdown file, and neither can reach a database. (A third, optional agent sits *outside*
the run: `prompts/draft-rules.md` turns `synthgen patch survey` output into draft rule
proposals under `rules/drafts/` — drafts, never rules; see the runbook §0.)

### Agent 1 — plan narrator

Called by `run.ps1` after `synthgen patch plan` has already produced `plan.json`:

```powershell
copilot -p prompts/plan.md --model $env:PFANDWERK_MODEL_MAKER
```

Reads `artifacts/plan.json` and `rules/gaps.yaml`; writes `artifacts/plan-summary.md`.
The numbers and values already exist — the agent is arranging them for a human reader, not
computing them.

### Agent 2 — report maker

Called by `run.ps1` before `synthgen patch report` extracts the facts and audits the result:

```powershell
copilot -p prompts/report-maker.md --model $env:PFANDWERK_MODEL_MAKER
synthgen patch report                  # deterministic C#, not a second model
```

Reads `artifacts/facts.json`; writes `artifacts/report.md`. `ReportAuditor` then checks
every number and rule id in the prose against `facts.json`. Fail twice and the pipeline
publishes a bare rendering of `facts.json` instead, noting that the narrative failed audit.

**There is no checker agent.** An earlier draft had one; D9 replaced it with `ReportAuditor`
because mechanical number-matching is exactly what deterministic code does perfectly and a
model does probabilistically — and that code had to exist anyway for the CI notary.

### What the agents load

Both prompts sit on top of `AGENTS.md`, which carries the nine hard rules verbatim and no
per-run data. That stability is deliberate: it keeps the prompt-cache prefix identical
across runs.

### The boundary that makes this safe

Prompt instructions are not a security boundary — a model that ignores them is not
misbehaving in a way instructions can prevent. The P0b spike
([docs/history/copilot-cli-findings.md](history/copilot-cli-findings.md)) found that Copilot CLI has **no hook mechanism**, so the
originally planned pre/post-tool-use hooks were never built. Containment comes from the
CLI's own permission system instead, composed by `run.ps1` on every agent invocation:

- **`--available-tools=shell,write`** — nothing else is visible to the model at all; this
  is the deny-by-default primitive.
- **`--add-dir artifacts/`** — writes are scoped to the artifacts directory.
- **`--deny-url`** — network egress is closed.
- Explicit `--deny-tool` patterns still win over everything, including `--allow-all-tools`
  (which non-interactive mode requires).

### Where Copilot runs

Locally, always. It is never installed in GitLab CI, which runs one job — the deterministic
artifact audit. This is not only policy: in the sandbox this repository is developed in,
`api.githubcopilot.com` is refused at the network layer, so a pipeline that depended on it
would fail closed rather than silently degrade.

## 6. One run, end to end, by process

```
run.ps1
├─ synthgen patch plan     ConnectionAllowlist                          → exit 4 on mismatch
│                     ScriptDom → GapQuery → Dapper/SqlClient
│                     Bogus/FakerMap  (ephemeral, identity)
│                     pure functions  (derived)
│                     ledger + target collision checks              → plan.json
│                     invariant + consumer checks                   → baseline.json
├─ copilot -p prompts/plan.md                                       → plan-summary.md
├─ synthgen patch approve  human reads tiers 1 and 2                    → plan.approved
├─ synthgen patch apply    re-verify sha256, then IPatchSink per row     → patches.jsonl
│                     SqlPatchSink: ledger + target in one tx
├─ synthgen patch verify   GapQuery re-scan          (layer 1)           → exit 10
│                     xUnit invariants          (layer 2)           → exit 20
│                     xUnit consumer suite      (layer 3)           → exit 30
│                     diffed against baseline.json                  → verify.json
├─ synthgen patch report   FactExtractor → ReportAuditor → fallback      → facts.json
├─ copilot -p prompts/report-maker.md                               → report.md
                                                                    → report.audit.json

GitLab CI
└─ synthgen patch notary   ArtifactAuditor over committed artifacts. No DB, no Copilot.
```

Read the two `copilot` lines against the rest: both come **after** the deterministic step
whose output they describe, and neither has a downstream consumer that trusts it. The plan
summary informs a human who is also shown the raw numbers; the report is mechanically
audited before it ships.

## 7. Summary

- **Gaps are detected** by ScriptDom-validated predicates, built by `GapQuery`, executed
  through Dapper/SqlClient. Deterministic, three call sites, one implementation.
- **Values come from** Bogus via `FakerMap` (ephemeral, identity) or pure row functions
  (derived), all at PLAN time, all frozen into `plan.json`.
- **Writes go through** `IPatchSink` — `SqlPatchSink` transactionally by default, DAB only
  if a site requires it.
- **Agents write two markdown files**, after the fact, with no database access, one of
  which is mechanically audited before it can ship.
- **DAB is optional and never the default.** **Copilot is local-only.** **Bogus never
  runs during APPLY.**
