# PLAN narrative

Read `AGENTS.md` first. Its hard rules bind everything below.

## Task

1. Run: `synthgen patch plan`
2. Read `artifacts/plan.json`.
3. Write `artifacts/plan-summary.md` — a human-readable summary a reviewer reads *before*
   approving the run at the gate.

If `synthgen patch plan` exits non-zero, or `artifacts/plan.json` does not exist, write nothing.
Report the exit code and stop.

## Sources — and nothing else

| What you need | Where it comes from |
|---|---|
| counts, rule ids, table and column names, sample values, identity values, threshold state | `artifacts/plan.json` |
| the `reason` text for each rule | `rules/gaps.yaml` — copy it verbatim, do not paraphrase |

No other file is a source. Your own knowledge of property data, energy ratings, or
security identifiers is not a source.

## Structure

Open with one line: the run id, the target table, and the total number of rows that would
be patched.

Then one `##` section per rule, in the order the rules appear in `plan.json`. Each section:

- the rule id and the column it patches
- how many rows match, and the rule's threshold
- the `reason` text, verbatim from `rules/gaps.yaml`
- for ephemeral rules: up to 5 of the frozen values from `plan.json`, quoted exactly
- for identity rules: every new identity value, with the row key it is bound to

Close with a `## Blocked` section listing any rule whose `status` is `BLOCKED`, with its
count and threshold. If no rule is blocked, write `None.` — do not omit the section.

## What a reviewer is deciding

They are deciding whether to approve values that will be written to a database and, for
identity columns, frozen permanently. Two things make that decision possible:

- **The values are real.** Ephemeral values in `plan.json` are frozen — they are exactly
  what will be written. Quote them as such. Never describe them as examples, illustrations,
  or "values like these".
- **Identity values are permanent.** Once approved and applied they are never regenerated.
  Say so in the identity sections.

Write plainly. No preamble, no recommendation, no reassurance about quality. The reviewer
wants the numbers and the values, not an argument.

## Output contract

Write exactly one file: `artifacts/plan-summary.md`. Produce no other file and no other
output.
