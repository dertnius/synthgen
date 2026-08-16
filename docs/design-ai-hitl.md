# AI-authored gap repair with HITL

Status: accepted design, implemented 2026-08-16 (backlog items 1–6 below; see each
item's note). Decisions were resolved in a grill-me-2 interview the same day;
implementation landed after the simplification plan (S1–S6). One deliberate deviation
from D-C3: CUR-001 and its dataset were authored directly with the implementation
rather than by a live run of the authoring skill — the skill exists and lints the same
artifacts, but its first end-to-end authoring run is still ahead.

## Problem

The patch pipeline's genericity is bottlenecked in exactly one place, by design: every
new patched value requires an entry in the generator whitelist (`Generators.cs`), which
means a C# change, a rebuild, and a reviewed MR per table vocabulary (hard rules 1
and 9). For logic-bearing repairs that bottleneck is the point. For vocabulary-shaped
repairs — a Currency table missing ISO names, a Color column, an order-status domain —
it turns every new table into a code deployment.

The fix is two mechanisms, not one:

- **Vocabularies become reviewed data** (`rules/datasets/`), removing the recompile
  without removing the review.
- **Logic stays code, but an AI agent authors it**, and the existing pipeline —
  MR review, two-tier gate, thresholds, invariants, notary — is the verification
  harness. The hard rules were already shaped for this; the agent slots in as the
  *rules author*, and every mistake it can make has a distinct, later tripwire.

## Decisions

### D-A1 — Reviewed datasets live in `rules/datasets/`

A generic `dataset.<name>` fix key reads `rules/datasets/<name>.yaml`: rows of
correlated columns (e.g. currency `code` + `name`), optional weights. The rules file
still names a key and never carries a literal value, so hard rule 1 holds; dataset
files get the same reviewed-MR + notary treatment as `gaps.yaml`. Rejected: values
inline in `gaps.yaml` (breaks the documented rule the review story leans on) and
Bogus-locale-only (org-specific vocabularies stay impossible).

### D-A2 — One dataset format, two consumers

The same files serve SynthGen bulk generation and the patch pipeline: a `dataset`
strategy in `RowGenerator` (next to `pick`/`faker`, weighted, correlated columns) and
`dataset.<name>` fix keys in the patch whitelist. `ColumnInference` gains a name-match
hook (e.g. `*currency*` → `datasets/currencies`) beside the existing faker heuristics.
One review story for all vocabularies; a Currency column works in bulk generation
*and* repair from the same reviewed list.

### D-A3 — Existing pure value lists migrate

One MR moves `property.energyClass`, `adventureworks.productColor`, and
`adventureworks.orderStatus` from `Generators.cs` to `rules/datasets/` and deletes the
C# entries; the fix keys in any rules change name, nothing else. Logic-bearing
generators (`security.securityId`, `security.bathroomsFromRooms`,
`adventureworks.personId`) stay code. One home per kind of thing.

### D-B1 — The authoring workflow is one host-agnostic skill

Inputs: table + reported gap. Outputs: a three-part MR — `gaps.yaml` rule, generator
entry (only when no dataset or existing key fits), boundary tests. Defined once as a
skill so it runs in Claude Code today and slots into VSCODE-COPILOT-PLAN's
skills-first Agent Host later. No surface lock-in.

### D-B2 — The agent never holds a database connection

The authoring skill profiles gaps from **survey artifacts only**: the existing
`patch survey` verb, extended to emit per-column NULL counts and value samples, run by
the operator or CI. This corrects the illustrative walkthrough (which showed the agent
running free-form SELECTs) and keeps hard rule 6's deny-by-default posture: the
agent's inputs are whitelisted verb output, auditable, never raw SQL against real
data.

### D-B3 — `patch lint`: offline-only self-check

Before opening the MR, the skill validates its draft with a new verb that needs no
database: rule schema, ScriptDom AST inspection of gap *and* invariant, fix-key
existence (datasets included), threshold sanity against the surveyed count, and
test-convention presence (refuse-NULL test, boundary theory). CI covers the DB half.
Rejected: lint-with-dry-run (two code paths, and the POC has no local SQL Server) and
CI-only validation (the agent would iterate by pushing broken MRs into the history the
notary audits).

### D-C1 — The agent is the rules author; that makes the two-party rule real

The skill commits rules MRs under its own bot identity. The notary's
approver ≠ rules-author check then compares bot-author against human-approver —
enforceable with a single human, no theater. Production tightens to human-vs-human
with no design change. Rejected: advisory mode (the check never gates anything in the
POC) and a second git identity for approvals (documents how to defeat the control).

### D-C2 — Test conventions are the review contract

Every AI-authored generator ships: a boundary-value theory (the spec's named edges),
a refuse-NULL-input test, a refuse-unknown-input test, and — for derived rules — a C#
generator and SQL invariant written independently so their disagreement is an alarm
verify can ring. Reviewers get a checklist, not archaeology; `patch lint` checks the
tests exist, CI checks they pass.

### D-C3 — The Currency example becomes the E2E proof

Backlog, after simplification lands: seed 14 NULL `Name` rows plus one garbage `ZZZ`
row in the CI AdventureWorks database, let the authoring skill draft CUR-001 and its
generator itself, and let the existing GitHub Actions flow publish plan / gate /
verify / notary evidence to the gh-pages dashboard. The skill authoring its own E2E
fixture is the proof of the whole design: predicate coverage, the skipped ZZZ row,
threshold gating, and the notary's bot-vs-human check all fire on real artifacts.

## Out of scope

- **A safe expression language for derived rules.** One expression used for both
  generation and verification verifies nothing — the current design's power is that
  the C# generator and the SQL invariant can disagree.
- **Free-form agent SQL and agent-held connections** (D-B2).
- **Literal values in `gaps.yaml`** — the rule file names keys, forever.
- **Auto-approval of any kind.** Both signatures stay human.

## Backlog

Ordered; item 1 blocks 2–3 only where noted. All sequenced after S1–S6.

1. **Datasets** — DONE: `Datasets.cs` (loader, schema, weighted picks, correlated
   lookup, `match` globs); `dataset.<name>` fix keys validated in `GapRulesLoader` and
   planned in `Planner`; `dataset` strategy in `RowGenerator` (correlated per row);
   `ColumnInference` name-match hook; the three pure lists migrated out of
   `Generators.cs` (D-A1..A3).
2. **Survey extension** — DONE before this landed: `patch survey` already emits
   per-column NULL counts and top-value samples (D-B2); no further change was needed.
3. **`patch lint`** — DONE: offline verb (schema, AST, fix keys incl. datasets,
   threshold vs. surveyed NULL count, D-C2 test conventions), full finding list in
   `lint.json`, non-zero exit; also runs in CI (D-B3).
4. **Authoring skill** — DONE: `.github/skills/author-gap-rule/SKILL.md` — survey
   artifacts in, three-part MR out, self-checked with `patch lint`, committed as the
   bot identity. Its first live authoring run is still ahead (see Status).
5. **Notary author check** — DONE: `ArtifactAuditor` four-eyes now reads the git
   author of the rules file *and* its `datasets/` directory; the bot identity is
   pinned in AGENTS.md hard rule 9 and the skill (D-C1).
6. **Currency E2E** — DONE as fixture + pipeline proof: `Sales.Currency` generated
   from `datasets/currencies.yaml`, the corrupt verb seeds 14 NULL names plus the
   garbage ZZZ row, CUR-001 repairs by dataset lookup, the ZZZ row is skipped at the
   gate, and the existing CI flow publishes the evidence to the dashboard (D-C3).

## Provenance

Nine decisions, resolved 2026-08-16: 5 changed the pre-interview design, 2 refined
it, 2 covered ground it missed, 0 were confirmed unchanged — the original analysis
was directionally right and wrong in every particular worth asking about. The
interview ledger (question, before, after, rationale per row) is preserved in the
session records; this document is the durable form.
