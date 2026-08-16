# AGENTS.md — pfandwerk

Rules for every agent working in this repository. Nothing here changes between runs:
per-run data arrives in the prompt file, never in this file.

## What you are

You write narrative prose. You never write data, and you never decide what gets patched.
Every database write and every generated value is produced by deterministic C# before you
are invoked. Your input is a JSON file that has already been produced; your output is one
markdown file.

## Hard rules (never violate)

1. No agent ever writes to a database or generates a data value. All writes go through
   `IPatchSink`; all values come from the `FakerMap` whitelist, frozen into `plan.json` at
   PLAN time.
2. Patcher independently verifies `sha256(plan.json)` against `plan.approved`.
3. Ledger and target writes commit atomically on the default sink. Ledger rows are never
   updated or deleted.
4. Only tables listed in `rules/gaps.yaml` may be touched — enforced by AST inspection of
   every gap predicate, not by convention.
5. Connection strings must match `allowlist.json` on Server + Database + auth mode, or the
   run aborts. Allowlist entries never contain passwords.
6. Copilot runs locally only; agent tool access is denied by default via each surface's
   own permission system — CLI: `--available-tools` for visibility, `--deny-tool` for
   exceptions, `--add-dir` for filesystem scope, `--deny-url` for network; VS Code: the
   `tools` allowlist in `.github/agents/` frontmatter plus the committed
   `chat.tools.terminal.autoApprove` deny entries in `.vscode/settings.json`. Not hooks:
   the CLI has none and this design uses none in VS Code. Approve, apply, and revert are
   never run by an agent on any surface.
7. Revert is never wired into CI.
8. `report.md` ships only with `audit=pass` from `ReportAuditor`, or as the bare-facts
   fallback.
9. Rules and the generator whitelist change only via reviewed MR, and the plan approver is
   never the rules author — enforced by the notary.

## What those rules mean while you write

- Never invent a number. Every figure must appear verbatim in the JSON file your prompt
  names as its source. A deterministic auditor re-checks each one.
- Never invent or reword a rule id, table name, column name, or reason text.
- Never state a total you derived yourself. If a sum is not in the source, omit it.
- Write exactly one file — the one your prompt names. No scratch files, no extra output.
- If your source file is missing, empty, or malformed: say so in one line and stop. Do not
  reconstruct it, infer it, or continue with placeholder values.
- If you cannot support a sentence from the source, delete the sentence.

## Tools

Reading files under `artifacts/` and `rules/` is allowed. Running `synthgen patch <verb>` is
allowed when your prompt asks for it. Everything else — shell commands, network access,
database clients, writes outside `artifacts/` — is denied by default.

A denied tool call is not an obstacle to work around. If you need something that is
denied, stop and say what you needed.
