# DRAFT RULES

Read `AGENTS.md` first. Its hard rules bind everything below.

## Task

Read `artifacts/survey.json` (produced by `synthgen patch survey`) and write one draft rules file
per table into `rules/drafts/<table>.yaml`.

A draft is a **proposal for a person to correct**, not a rule. Nothing you write reaches a
database. Someone reads it, edits it, moves what survives into `rules/gaps.yaml`, and opens
a merge request — and even then four separate mechanisms get to reject it before a single
row changes.

If `artifacts/survey.json` does not exist or does not parse, write nothing. Say so and stop.

## Source — and nothing else

`artifacts/survey.json` is your only source of facts about the data. It gives you, per
column: type, nullability, NULL count and rate, distinct count, the most frequent values,
whether an existing rule already covers it, and a list of neutral observations.

Your knowledge of what a column *called* `EnergyClass` usually contains is useful for
writing the `reason` text. It is **not** evidence about this database. Never state a number
that is not in `survey.json`.

## What to skip

- Any column where `coveredByRule` is set. A rule already exists; leave it alone.
- Primary keys, identity columns, and anything with no `signals`.
- Columns whose only signal is a low NULL rate on a genuinely optional field. Not every
  NULL is a defect — an optional middle name is not broken.

Skipping is the common case. A draft naming three real defects is worth more than one
naming thirty maybes, and a reviewer who has to delete twenty-seven entries stops reading.

## Choosing `kind`

This is the field you are most likely to get wrong, so state your reasoning in a comment
above each rule.

| kind | Choose it when | Danger |
|---|---|---|
| `ephemeral` | any plausible value satisfies the tests | none — it is the safe default |
| `derived` | the value follows from other columns in the same row | you must name the `inputs`, and the formula must be real |
| `identity` | other systems join on this value | **one-way door** — it enters the ledger and every future run reuses it |

**Default to `ephemeral`.** Propose `identity` only when the column name and the survey
together make it obvious (a reference, a code, near-unique, rarely NULL), and say plainly in
the comment that it is permanent. Getting this wrong costs a person far more than a
too-cautious draft.

## Choosing `fix`

Run `synthgen patch generators` and use only a key it lists. If nothing fits, write the rule with
the `fix` you would want and add a comment saying it needs a new generator — that is a code
change and a reviewed MR, which is deliberate: a rule file may never carry a literal value.

Do not invent a key and hope. An unknown `fix` fails at load, immediately, by design.

## Choosing `threshold`

Set it a little above the count the survey reports, and say in the comment what the count
was. The threshold is the reviewer's alarm against a predicate that matches far more than
expected — a number near infinity disables it, and a number below the current count blocks
the run on day one.

## Writing `gap`

A SQL boolean predicate over **this table only**. Cross-table subqueries are rejected when
the rules load. Base it on what the survey actually shows: if a column is 34% NULL and 12%
`'X9'`, the predicate is about NULL and `'X9'`, not about whatever else you imagine.

Prefer narrowing by a status column when the survey shows one. `SignedDate IS NULL` and
`SignedDate IS NULL AND Status = 'active'` are different rules, and the second is usually
what someone means.

## Output shape

One file per table, valid YAML, every rule commented with the survey evidence behind it:

```yaml
# Drafted from artifacts/survey.json — NOT a rule until a person edits and moves it.
rules:
  # 412 rows NULL of 1200 (34%), plus 'X9' in 12% of the rest.
  # ephemeral: the tests only need a value present.
  - id: HOU-001
    table: dbo.House
    key: HouseId
    column: EnergyClass
    kind: ephemeral
    gap: "EnergyClass IS NULL OR EnergyClass = 'X9'"
    fix: property.energyClass
    threshold: 600
    reason: >-
      Describe why this is broken and why a test cares. One or two sentences.
```

Use the table's existing rule-id prefix if one exists in `rules/gaps.yaml`; otherwise take
the first three letters of the table name.

## What you must not do

- Do not write to `rules/gaps.yaml`. Drafts go to `rules/drafts/` only.
- Do not run `synthgen patch plan`, `apply`, or anything that touches a database.
- Do not invent counts, rates or values. Every number you write comes from `survey.json`.
- Do not propose a rule for a column you cannot justify from the survey.

## Output contract

Write one file per surveyed table into `rules/drafts/`, and nothing else. If a table
warrants no rules, write no file for it and say so.
