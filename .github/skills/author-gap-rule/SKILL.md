---
name: author-gap-rule
description: Author a complete gap-repair MR from survey artifacts — gaps.yaml rule, dataset or generator entry, boundary tests — self-checked with `synthgen patch lint` and committed under the pfandwerk bot identity. Use when asked to author, implement, or ship a repair rule for a reported gap (not merely to draft a proposal — that is draft-rules).
argument-hint: "<table> <reported gap, e.g. 'Sales.Currency Name is NULL'>"
---

# /author-gap-rule

Read `AGENTS.md` first — its hard rules bind everything below. This skill is the D-B1
workflow of `docs/design-ai-hitl.md`: the agent is the *rules author*; the existing
pipeline (lint, reviewed MR, two-tier gate, thresholds, invariants, notary) is the
verification harness.

## Inputs — survey artifacts only, never a database

Profile the gap exclusively from `artifacts/survey.json` (D-B2). If it is missing or
stale, stop and ask the operator (or CI) to run `synthgen patch survey` — you never open
a connection, run SQL, or pass `--target`. The only verbs you may run are the offline
ones: `synthgen patch generators` and `synthgen patch lint`.

## Decide where the fix lives — one home per kind of thing (D-A3)

1. **An existing key fits** (`synthgen patch generators` lists them) → name it, done.
2. **The repair is a vocabulary** — a value list, correlated columns like code+name →
   a reviewed dataset in `rules/datasets/<name>.yaml` (`columns`, `rows`, optional
   `weights`/`match`), reached as the `dataset.<name>` fix key. Ephemeral rules pick a
   weighted random row; derived rules declare `inputs` and look the row up. No C#.
3. **The repair is logic** — computed from the row's own columns → a generator in
   `src/SynthGen.Core/Pfandwerk/Generators.cs`, plus tests (below).

## The three-part MR

- **Rule** in `rules/gaps.yaml`: id (table's existing prefix), table, key, column, kind,
  gap, fix, threshold justified by the surveyed count, reason in consumer terms. The
  rule names a key; it never carries a literal value.
- **Dataset or generator entry** — only when no existing key fits.
- **Tests** (D-C2, for derived code generators): a boundary-value `[Theory]` naming the
  spec's edges, a refuse-NULL-input test (name it `..refuse.._null..` so the convention
  is visible), a refuse-unknown-input test. Write the C# generator and the SQL
  `invariant` independently — their disagreement is an alarm VERIFY must be able to ring.
  Dataset fixes need no tests; the vocabulary itself is the reviewed artifact.

## Self-check before the MR (D-B3)

Run `synthgen patch lint` (add `--rules`/`--tests` if paths differ) and fix **every**
finding, then `dotnet test`. Iterate here, not by pushing broken MRs into the history
the notary audits.

## Commit as the bot, open the MR (D-C1)

The pinned rules-author identity — the notary compares it against the human approver,
which is what makes the two-party rule real with a single human:

```
git -c user.name="pfandwerk-bot" -c user.email="pfandwerk-bot@users.noreply.github.com" \
    commit -m "<rule id>: <one-line gap>"
```

Commit on a fresh branch, push, and open the MR with `gh pr create`, describing: the
surveyed evidence, the chosen fix key and why, the threshold arithmetic, and what a
reviewer must check (the D-C2 checklist). Never merge it, never approve anything, and
never run `synthgen patch plan`, `approve`, `apply`, or `revert`.
