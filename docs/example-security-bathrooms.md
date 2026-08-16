# Worked example — `dbo.Security`, derived bathrooms and frozen identifiers

An end-to-end run over one table with two rules of different kinds. It exists to show how
every part of pfandwerk fits together on data you can read in full, and to record two
design changes this example forced.

Fixture: [`security.sql`](../db/pfandwerk/fixtures/security.sql) ·
[`security-broken-seed.sql`](../db/pfandwerk/fixtures/security-broken-seed.sql) ·
rules: [`rules/gaps.yaml`](../rules/gaps.yaml) (SEC-001, SEC-002)

## 1. The data

| PropertyId | SecurityId | PropertyType | Rooms | Bathrooms | |
|---|---|---|---|---|---|
| 101 | DE0001234567 | EFH | 3 | **NULL** | SEC-001 gap |
| 102 | **NULL** | EFH | 5 | **NULL** | SEC-001 + SEC-002 |
| 103 | DE0007654321 | EFH | 7 | **NULL** | SEC-001 gap |
| 104 | **NULL** | EFH | **NULL** | **NULL** | SEC-001 unusable + SEC-002 |
| 105 | DE0009999999 | EFH | 9 | **NULL** | SEC-001 gap |
| 106 | DE0001111111 | MFH | 12 | 4 | control — not EFH |
| 107 | DE0002222222 | WHG | 2 | 1 | control — not EFH |
| 108 | DE0003333333 | EFH | 4 | 2 | control — already correct |

Row 108 is the one that proves the gap predicate does not over-select: it is an EFH whose
bathroom count is already right, and a run must leave it byte-identical.

## 2. The two rules

**SEC-002 — `SecurityId`, kind `identity`.** Gap is `SecurityId IS NULL`; matches 102 and
104. A generated identifier is written to the ledger and frozen: a second run reuses it,
forever. This is the kind where being wrong is expensive, because downstream systems join
on the value.

**SEC-001 — `Bathrooms`, kind `derived`.** Gap is `PropertyType = 'EFH' AND Bathrooms IS
NULL`; matches 101–105. The value is not random — it follows from `Rooms`:

```
Bathrooms = max(1, ceil(Rooms / 3))
```

One bathroom minimum, one more per three rooms. In T-SQL and SQLite integer arithmetic:

```sql
CASE WHEN Rooms IS NULL OR Rooms < 1 THEN 1 ELSE (Rooms + 2) / 3 END
```

| Rooms | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|---|---|---|---|---|---|---|---|---|---|
| Bathrooms | 1 | 1 | 1 | 2 | 2 | 2 | 3 | 3 | 3 | 4 |

Three rooms gives one bathroom; four gives two, as specified.

## 3. What this example changed in the design

Two things, both recorded rather than slipped in.

**Cross-column coherence was out of scope.** `PLAN.md` §10 excluded it. A value derived
from another column in the same row *is* cross-column coherence, so the exclusion no
longer holds and §10 drops it.

**`Func<Faker, object>` cannot express a derived value.** The generator whitelist takes a
`Faker` and nothing else — it has no access to `Rooms`, so no entry in it could ever
produce this. Derived rules therefore need a **second, separate** whitelist:

```csharp
// ephemeral — random, reproducible only because the plan freezes the result
Dictionary<string, Func<Faker, object>>
// derived — a pure function of the row's declared inputs
Dictionary<string, Func<IReadOnlyDictionary<string, object?>, object>>
```

Keeping them separate is deliberate. Widening the ephemeral signature so it *could* read
the row would make any ephemeral generator silently order-dependent, and its output would
stop being reproducible from `plan.json` alone — which is the property D2 exists to
protect.

A derived rule also carries three fields the other kinds do not: `inputs` (which columns
the generator may read), `onMissingInput`, and `invariant`.

## 4. The run, phase by phase

### GUARD

`ConnectionAllowlist` parses the connection and matches Server + Database + auth mode
against `allowlist.json`. No match, exit 4, nothing else runs. This is the only thing
standing between a mistyped connection string and a patched production table.

### SCAN

Runs each rule's gap predicate through `GapQuery` — the single code path that turns a
predicate into SQL, so SCAN, PLAN and VERIFY cannot disagree about what a gap is. For a
derived rule it also selects the `inputs` columns.

```jsonc
// the rows plan.json is built from
{
  "runId": "2026-08-15T13:02:11Z-a4f1",
  "rules": [
    { "id": "SEC-001", "table": "dbo.Security", "column": "Bathrooms", "count": 5,
      "rows": [
        { "key": { "PropertyId": 101 }, "current": null, "inputs": { "Rooms": 3 } },
        { "key": { "PropertyId": 102 }, "current": null, "inputs": { "Rooms": 5 } },
        { "key": { "PropertyId": 103 }, "current": null, "inputs": { "Rooms": 7 } },
        { "key": { "PropertyId": 104 }, "current": null, "inputs": { "Rooms": null } },
        { "key": { "PropertyId": 105 }, "current": null, "inputs": { "Rooms": 9 } }
      ] },
    { "id": "SEC-002", "table": "dbo.Security", "column": "SecurityId", "count": 2,
      "rows": [ { "key": { "PropertyId": 102 }, "current": null },
                { "key": { "PropertyId": 104 }, "current": null } ] }
  ]
}
```

SCAN also captures `baseline.json` — the invariant and consumer suites run *before*
anything changes, through the same `TestRunner` that VERIFY will use afterwards. SEC-001's
invariant is already failing on 101–105:

```jsonc
// artifacts/baseline.json  (extract)
{ "invariants": [ { "name": "SEC-001.invariant", "passed": false,
                    "violations": [101, 102, 103, 104, 105] } ],
  "consumer": [ { "name": "Security.EfhBathroomsNotNull", "passed": false },
                { "name": "Security.SecurityIdUnique",    "passed": true } ] }
```

Capturing this *before* the run is what makes the difference between "we broke it" and "it
was already broken" a fact rather than an argument.

### PLAN

Deterministic C#. No model is involved in choosing a single value.

*Identity (SEC-002).* For each row: look up `(dbo.Security, PropertyId, SecurityId)` in the
ledger. Miss → generate from the whitelist, then collision-check against both the live
table and the ledger before accepting.

*Derived (SEC-001).* Apply `security.bathroomsFromRooms` to the declared inputs. Row 104's
`Rooms` is NULL, so the generator cannot run; `onMissingInput: block` means the row is
**skipped**, not guessed.

Every value — identity and derived alike — is **frozen into `plan.json` now**, before any
human looks at it. That is what makes the SHA-256 approval mean something.

```jsonc
// artifacts/plan.json
{
  "runId": "2026-08-15T13:02:11Z-a4f1",
  "rulesSha": "b19c…",
  "rules": [
    { "id": "SEC-001", "status": "OK", "count": 5, "threshold": 500,
      "patches": [
        { "key": { "PropertyId": 101 }, "value": 1, "inputs": { "Rooms": 3 } },
        { "key": { "PropertyId": 102 }, "value": 2, "inputs": { "Rooms": 5 } },
        { "key": { "PropertyId": 103 }, "value": 3, "inputs": { "Rooms": 7 } },
        { "key": { "PropertyId": 105 }, "value": 3, "inputs": { "Rooms": 9 } }
      ],
      "skipped": [
        { "key": { "PropertyId": 104 }, "why": "input 'Rooms' is NULL",
          "policy": "onMissingInput: block" }
      ] },
    { "id": "SEC-002", "status": "OK", "count": 2, "threshold": 50,
      "newIdentities": [
        { "key": { "PropertyId": 102 }, "value": "DE000K7M2P41" },
        { "key": { "PropertyId": 104 }, "value": "DE000R3X9T05" }
      ] }
  ]
}
```

The hash is recomputed on demand rather than stored; `plan.approved` records the one that was approved.

> **Spec refinement this forced.** VERIFY layer 1 was specified as "planned rules must be 0
> gaps". Row 104 is still a gap afterwards, which would exit 10 on a correct run. L1
> therefore compares against the **planned** row set, not the raw predicate count. A row
> deliberately skipped is not a failure to close a gap; it is a gap nobody agreed to close.

### GATE — `approve.ps1`

The one place a human decides. Tier 1 lists every new identity in full, because those are
permanent. Tier 2 gives counts, the frozen values, and threshold state.

```
SEC-002  identity  2 new  (threshold 50)
  PropertyId 102 -> DE000K7M2P41      PERMANENT — reused by every future run
  PropertyId 104 -> DE000R3X9T05      PERMANENT — reused by every future run

SEC-001  derived   4 to patch, 1 skipped  (threshold 500)
  101  Rooms 3 -> Bathrooms 1
  102  Rooms 5 -> Bathrooms 2
  103  Rooms 7 -> Bathrooms 3
  105  Rooms 9 -> Bathrooms 3
  SKIPPED
  104  Rooms NULL -> cannot derive     (onMissingInput: block)

Approve? [yes/no]
```

The reviewer sees the inputs next to the outputs, which is the only way to judge a derived
value — `Bathrooms 3` alone is unreviewable; `Rooms 7 -> 3` is checkable at a glance.

On `yes`, `plan.approved` is written with the hash, OS user, git email and UTC timestamp.

### APPLY

`Patcher` recomputes `sha256(plan.json)` and compares it to `plan.approved`. Mismatch →
abort. It never trusts that the gate ran before it; a plan edited after approval is
rejected even if the scripts ran in the right order.

Then, per row through `IPatchSink`: `SqlPatchSink` opens **one transaction** covering the
ledger INSERT (identities only) and the target UPDATE, and commits both or neither.

```jsonc
// artifacts/patches.jsonl  (one line per change)
{"ts":"…","rule":"SEC-002","id":{"PropertyId":102},"col":"SecurityId","old":null,"new":"DE000K7M2P41","reason":"SecurityId is the stable external reference…"}
{"ts":"…","rule":"SEC-002","id":{"PropertyId":104},"col":"SecurityId","old":null,"new":"DE000R3X9T05","reason":"…"}
{"ts":"…","rule":"SEC-001","id":{"PropertyId":101},"col":"Bathrooms","old":null,"new":1,"reason":"An EFH is a single-family house…"}
{"ts":"…","rule":"SEC-001","id":{"PropertyId":102},"col":"Bathrooms","old":null,"new":2,"reason":"…"}
{"ts":"…","rule":"SEC-001","id":{"PropertyId":103},"col":"Bathrooms","old":null,"new":3,"reason":"…"}
{"ts":"…","rule":"SEC-001","id":{"PropertyId":105},"col":"Bathrooms","old":null,"new":3,"reason":"…"}
```

Ledger after the run — two rows, never updated, never deleted:

| TargetTable | RowKey | ColumnName | Value | RuleId |
|---|---|---|---|---|
| dbo.Security | 102 | SecurityId | DE000K7M2P41 | SEC-002 |
| dbo.Security | 104 | SecurityId | DE000R3X9T05 | SEC-002 |

Note 104: it got its identifier even though its bathrooms were skipped. The rules are
independent, and one unusable input does not hold up an unrelated repair.

### VERIFY

**Layer 1 — re-scan.** Every *planned* row is checked through `GapQuery` again. All four
SEC-001 patches and both SEC-002 patches closed. Row 104's bathrooms remain a gap, and are
expected to.

**Layer 2 — invariants.** SEC-001's invariant now fails only on 104. Diffed against the
baseline, which failed on 101–105:

```
regressions:      (none)
pre-existing red: 104   — also failing before the run
```

Pre-existing reds are **listed, never fatal**. VERIFY exits 0.

This is the mechanism from D5 doing exactly what it was built for. Without a baseline, 104
would look like damage the run caused and would block a correct run indefinitely.

The invariant is also NULL-safe by construction, and it has to be. Written the obvious way —

```sql
PropertyType <> 'EFH' OR Bathrooms = CASE … END      -- WRONG
```

— `Bathrooms = <expr>` is UNKNOWN wherever `Bathrooms` is NULL, `NOT(UNKNOWN)` is UNKNOWN,
and the violation query returns nothing. The invariant would report perfect health on the
entirely broken seed. The `Bathrooms IS NOT NULL AND …` guard is what makes it detect the
rows it exists to detect. This was caught by running it against the fixture, not by reading
it.

**Layer 3 — consumer suite.** `Security.EfhBathroomsNotNull` was red at baseline and is
still red because of 104: pre-existing, listed, not fatal.

### REPORT

`FactExtractor` builds `facts.json` from `patches.jsonl` + `verify.json` + `plan.approved`
+ the rules. Deterministic, golden-file tested — no model touches it.

The Copilot maker agent then writes `report.md` from `facts.json` alone, and
`ReportAuditor` — deterministic C#, not a second model — checks every number and rule id in
the prose against `facts.json`. One invented figure, one dropped rule, and the report does
not ship: it falls back to a bare rendering of `facts.json`.

The maker is explicitly forbidden from merging regressions with pre-existing failures. On
this run that distinction is the entire story: "we changed 6 values and broke nothing" is
true, and "one row is still bad" is also true, and a report that blurs them is worse than
no report.

### AUDIT (CI notary)

Recomputes the plan hash, re-runs `FactExtractor` over the committed `patches.jsonl` and
diffs it against the committed `facts.json`, re-runs `ReportAuditor`, checks `rulesSha`,
and fails if the approver is the same person who last changed `rules/gaps.yaml`.

Per D13 this catches drift and accident, not collusion — it verifies that a set of files
one actor produced is internally consistent.

### REVERT (local only, never CI)

Replays `patches.jsonl` backwards through the same `IPatchSink`. All six values return to
NULL. **The two ledger rows stay.** So a second run re-scans, finds 102 and 104 missing
their SecurityId, looks in the ledger, gets a hit, and reuses `DE000K7M2P41` and
`DE000R3X9T05` — the identical identifiers, because that is what "frozen forever" means.

The derived bathrooms, by contrast, are simply recomputed from `Rooms` and come out the
same because the function is pure. They need no ledger: a derived value is always
reconstructible from the row.

## 5. Why each kind is treated differently

| | `identity` | `derived` | `ephemeral` |
|---|---|---|---|
| Value comes from | whitelist + collision check | pure function of `inputs` | whitelist, frozen at PLAN |
| Ledger | yes — permanent | no | no |
| Stable across runs | forever | yes, if inputs unchanged | yes, once patched |
| Reviewer needs to see | every value | inputs beside outputs | samples + count |
| Independently checkable afterwards | uniqueness | **yes — a full-table invariant** | no |

The last row is the practical payoff of a derived rule. Because the value is a function of
the row, correctness can be asserted over *every* row in the table, not only the ones this
run touched — which is why SEC-001 carries an `invariant` and the other kinds do not.

## 6. Second run, same fixture

1. GUARD passes.
2. SCAN: SEC-001 finds 1 gap (104), SEC-002 finds 0 — 102 and 104 have identifiers now.
3. PLAN: SEC-001 skips 104 again, so there is nothing to patch. Nothing is proposed.
4. GATE: nothing to approve.
5. VERIFY: layer 1 has no planned rows; layer 2 and 3 still list 104 as pre-existing.
   Exit 0.

The run is idempotent, and it is honest about the one row it cannot fix. Fixing 104 means
supplying its `Rooms` value upstream, or changing the rule's `onMissingInput` to `floor`
via reviewed MR — a decision for a person, which is precisely why the default is `block`.
