# Runbook — adding a new table and running it

How a person takes a table with bad data and gets it repaired. Every command here was run
against a real fixture while writing this; the output shown is the actual output.

## The short version

```bash
pfandwerk generators                 # 1. does a generator already exist for this column?
$EDITOR rules/gaps.yaml              # 2. write the rules
./run.ps1 -Only TEN-001,TEN-002      # 3. run — you approve at the gate
```

**Most new tables need no code.** Steps 1–3 are the whole job when the columns you are
repairing can be filled by a generator that already exists. A new *domain* generator is a
C# change and a reviewed MR (hard rule 9), which is deliberate — the rule file can never
carry a literal value itself, or the whole "values come only from the code-side whitelist"
guarantee evaporates.

---

## 1. Check whether a generator already exists

```
$ pfandwerk generators

Random generators — for kind: ephemeral and identity
  address.city
  address.country
  ...
  internet.email
  internet.userName
  ...
  property.energyClass
  security.securityId

Derived generators — for kind: derived (declare `inputs`)
  security.bathroomsFromRooms
```

Roughly forty entries, inherited from SynthGen's curated Bogus map plus pfandwerk's own.
If your column is an email, a city, a name, a phone number, an IBAN — it is already there
and you write no code at all.

If it is not, see [§6](#6-when-you-need-a-new-generator).

## 2. Write the rules

Add to `rules/gaps.yaml`, or keep your own file and pass `--rules`. A worked example for a
table that did not exist until this paragraph:

```yaml
rules:
  - id: TEN-001
    table: dbo.Tenant
    key: TenantId
    column: ContactEmail
    kind: ephemeral
    gap: "ContactEmail IS NULL AND Status = 'active'"
    fix: internet.email
    threshold: 100
    reason: >-
      The onboarding interface leaves ContactEmail empty when the tenant signs up by
      phone. Notification tests select on it and fail on the NULL.
```

The fields that need thought:

| Field | What to put |
|---|---|
| `id` | Stable. It appears in every artifact and in the report; changing it later orphans your history |
| `gap` | A SQL boolean predicate over **this table only**. Cross-table subqueries are rejected at load |
| `kind` | `ephemeral` (random, disposable) · `identity` (generated once, frozen forever, ledger-backed) · `derived` (computed from other columns of the same row) |
| `threshold` | Above this many matching rows the rule is BLOCKED and the gate refuses. Set it near what you expect, not near infinity — it is your protection against a predicate that matches the whole table |
| `reason` | Copied verbatim into the gate output and the report. Write it for whoever reviews the run at 5pm on a Friday |

**`kind: identity` is a one-way door.** The value goes into the ledger and every future run
reuses it. Use it only for columns other systems join on.

**`kind: derived`** additionally needs `inputs` (which columns the generator may read) and
optionally `onMissingInput` (`block`, the default, leaves the row alone and lists it; `floor`
writes the generator's documented minimum) and `invariant` (a predicate checked across the
whole table in VERIFY layer 2).

## 3. Point it at a database

Two connections. The ledger may be the same database or a separate config one.

```bash
export PFANDWERK_TARGET_CONNECTION="Server=.;Database=PropertyDev;Integrated Security=true;TrustServerCertificate=true"
export PFANDWERK_LEDGER_CONNECTION="Server=.;Database=PfandwerkConfig;Integrated Security=true;TrustServerCertificate=true"
```

Both must appear in `allowlist.json` or the run stops with exit 4 before touching anything.
Add yours under `targets` / `ledger` — **Server, Database and auth mode only, never a
password**; the file is committed and the loader rejects entries carrying credentials.

## 4. Dry run, then run

```bash
./run.ps1 -WhatIf -Only TEN-001,TEN-002      # walks every phase, writes nothing
./run.ps1 -Only TEN-001,TEN-002
```

Omit `-Only` to run every rule in the file. Add `-NoAgents` to skip the two Copilot steps —
the run is still complete, you just get the bare-facts report instead of prose.

You will be stopped at the gate:

```
=== GATE — run 20260815T140006Z-34b7 ===

TEN-001  ephemeral  dbo.Tenant.ContactEmail   [OK]
  3 gaps, threshold 100
  TIER 2 — values to be written:
    TenantId 1 -> Sasha36@yahoo.com
    TenantId 3 -> Allison_Wilkinson44@hotmail.com
    TenantId 5 -> Irving.Brown80@hotmail.com

Approve? [yes/no]
```

**Those are the actual values.** They were generated before you were asked and frozen into
`plan.json`; the SHA-256 you approve covers them, and APPLY re-checks that hash before it
writes anything. What you see is what lands.

For a `derived` rule the gate shows the inputs beside the outputs (`Rooms 7 -> 3`), because
`3` on its own is not something a person can check. For an `identity` rule it prints every
value in full and marks it permanent.

## 5. Read the outcome

```
VERIFY L1 pass  L2 pass  L3 pass
  regressions:            none
  pre-existing failures:  none
```

Exit codes: `0` clean · `10` planned gaps remain · `20` invariant regression · `30`
consumer-suite regression · `4` connection not allowlisted · `5` ledger conflict.

**Regressions and pre-existing failures are different things and never merged.** A
regression is something this run broke. A pre-existing failure was already failing when
`plan` captured the baseline — it is listed, and it does not fail the run. If you see a
pre-existing failure you did not expect, your data was already worse than you thought.

Artifacts land in `artifacts/`: `plan.json`, `baseline.json`, `plan.approved`,
`patches.jsonl`, `verify.json`, `facts.json`, `report.md`. Commit them if CI is to audit the
run.

### If you need to undo it

```bash
pfandwerk revert                     # everything, newest first
pfandwerk revert --only TEN-001      # one rule
```

Local only, never CI. Ledger rows are **not** deleted, so a later run reuses the same
identities rather than issuing new ones.

## 6. When you need a new generator

Only when nothing in `pfandwerk generators` fits — a domain value with its own rules, or
anything derived from another column.

1. Add the entry to `src/Pfandwerk/Generators.cs`:

```csharp
// random: ephemeral and identity
["billing.customerRef"] = f => "CR" + f.Random.Number(100000, 999999),

// derived: a pure function of the columns named in the rule's `inputs`
["security.bathroomsFromRooms"] = inputs => BathroomsFromRooms(inputs),
```

2. Add a unit test. Derived generators especially — if the generator and a rule's
   `invariant` ever disagree, the invariant fails on rows the generator just wrote.
3. Open an MR. Someone other than you approves it, and **that person must not be the one who
   approves the run** — the CI notary fails the build if the approver is the last person to
   have changed `rules/gaps.yaml`.

Derived generators take `Func<IReadOnlyDictionary<string, object?>, object>` and random ones
take `Func<Faker, object>`. The signatures are separate on purpose: a random generator that
could see the row would become order-dependent and stop being reproducible from `plan.json`,
which is the property that makes the approval hash mean anything.

## 7. Common stops

| What you see | What it means |
|---|---|
| `is not in the targets allowlist` (exit 4) | Add Server + Database + auth to `allowlist.json`. Never paste the whole connection string |
| `does not match the live schema` (exit 2) | A rule names a column the table does not have. Caught at SCAN rather than three phases later |
| `'gap' references dbo.X, but the rule declares only dbo.Y` | A predicate reached outside its table. Split it into two rules |
| `Unknown fix key` | Run `pfandwerk generators`; the message lists every valid key |
| `plan.json hash mismatch` | The plan changed after approval. Re-run the gate — this is the check working |
| `the ledger records X … but this run would write Y` (exit 5) | A frozen identity disagrees with what this run planned. Ledger rows are never updated, so a person has to decide |
| `N rule(s) BLOCKED past threshold` | More rows matched than the rule allows. Either the data got worse or the predicate is too broad — look before raising the threshold |

## 8. What a run does not do

It will not create a table, alter a schema, insert a row, or delete one. Every rule changes
one column in rows that already exist. If a table needs rows rather than repairs, that is
SynthGen's job — see the [README](../README.md).
