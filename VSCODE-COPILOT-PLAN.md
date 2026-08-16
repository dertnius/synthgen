# VS Code Copilot plan — driving the harness from the editor

Today the two agent steps run headless: `run.ps1` invokes `copilot -p prompts/<x>.md` with
CLI permission flags, and `AGENTS.md` reaches the model through the CLI's native loading.
This plan makes the same harness usable interactively from **VS Code Copilot Chat** — you
open the repo, type a slash command, and get the same constrained behavior — without
changing what `run.ps1` or CI does. Verified against the official VS Code docs
(pages last edited 2026-08-12) and the Copilot CLI reference; preview/experimental features
are flagged inline.

## 0. The one decision that shapes everything: skills, not prompt files

VS Code now has two agent runtimes: the legacy extension host and the new **Agent Host**
(`chat.agentHost.enabled`, under active development). The docs state plainly that **prompt
files (`.prompt.md`) do not work under the Agent Host** — the migration path is "convert
them to agent skills." Skills (`SKILL.md`, the open Agent Skills standard) load on **every
Copilot surface**: VS Code chat/agent mode, Copilot CLI, and the Copilot coding agent.

So the task layer is built as **skills**, not prompt files. This buys three things:

- One authoring format that survives the Agent Host transition.
- The same skills also load in Copilot CLI — the headless path can eventually converge on
  them instead of raw `-p` flags.
- Progressive disclosure: Copilot reads only `name` + `description` until a skill triggers,
  then loads the body, then reads referenced files on demand. The canonical prompts in
  `prompts/` become those referenced files — no duplication (Phase 4 discipline: one fact,
  one home).

## 1. Layer map

| Layer | File(s) | Loaded by | Status |
|---|---|---|---|
| Rules | `AGENTS.md` (exists, unchanged home) | VS Code natively (`chat.useAgentsMdFile`) **and** Copilot CLI natively | exists — needs one wording fix (§5) |
| Tasks | `.github/skills/<name>/SKILL.md` × 5 | VS Code, Copilot CLI, coding agent | new — §2 |
| Persona | `.github/agents/pfandwerk-scribe.agent.md` | VS Code agents dropdown | new — §3 |
| Permissions | `.vscode/settings.json` (committed) | VS Code | new — §4 |
| Canonical prompts | `prompts/*.md` | referenced by skills; still used by `run.ps1` | unchanged |

Not needed: `.github/copilot-instructions.md` (AGENTS.md is the same "always-on" tier —
adding both would restate the rules in two homes), MCP servers (the harness is CLI verbs,
no server to bridge), chat modes (`.chatmode.md` is deprecated; the format is `.agent.md`).

## 2. The skills

Five skills under `.github/skills/`. Frontmatter constraints that bite: `name` must equal
the parent directory name, lowercase/hyphens only — violations fail **silently**;
`description` (max 1024 chars) is what Copilot matches against for auto-discovery, so it
must say *when to use it*, not just what it is.

**Two knowledge skills** — auto-discoverable, they make the model competent about the
harness without pasting the README into every conversation:

| Skill | Body (short — points at the sole home of each fact) |
|---|---|
| `synthgen` | Generating synthetic data: `init` → `generate` → `evaluate`, the SQLite offline loop, exit codes → README. "Use when asked to create or validate test data from a DDL." |
| `pfandwerk-repair` | Repairing existing rows: the GUARD→SCAN→PLAN→GATE→APPLY→VERIFY→REPORT walk, **which steps are human-only** (the gate, revert), exit codes 4/5/10/20/30, where artifacts land → runbook. "Use when asked about repairing bad values in existing rows." |

**Three task skills** — thin wrappers over the canonical prompts, invoked as slash
commands. Each body is ~5 lines: "Read `AGENTS.md`; follow `prompts/<x>.md` exactly; its
output contract binds."

| Skill | Wraps | Frontmatter |
|---|---|---|
| `draft-rules` | `prompts/draft-rules.md` | `user-invocable: true` — `/draft-rules` after a survey |
| `plan-narrative` | `prompts/plan.md` | `user-invocable: true`, **`disable-model-invocation: true`** |
| `report-maker` | `prompts/report-maker.md` | `user-invocable: true`, **`disable-model-invocation: true`** |

`disable-model-invocation: true` on the two pipeline-phase skills matters: the narrative
steps run at a specific point in the spine, after the deterministic phase whose output they
describe. A model that helpfully "writes the report" mid-conversation would produce a file
`ReportAuditor` rejects (or worse, one it doesn't). Manual-only removes that failure mode;
`draft-rules` stays auto-invocable because drafting is safe by construction — drafts are
not rules.

Skip for now: `context: fork` (experimental, needs `github.copilot.chat.skillTool.enabled`).

## 3. The custom agent

`.github/agents/pfandwerk-scribe.agent.md` — the persona a user selects for repair work:

```yaml
---
name: pfandwerk-scribe
description: Narrative scribe for pfandwerk runs. Reads artifacts, writes prose, never data.
tools: ['search', 'read', 'edit', 'runCommands', 'todos']   # confirm exact ids in the /agents picker
agents: []                        # no subagents
disable-model-invocation: true    # a person picks it; other agents cannot summon it
---
Follow AGENTS.md. You draft rules, summarize plans, and write reports via the
draft-rules / plan-narrative / report-maker skills. You never run approve, apply,
or revert — those belong to the human at the gate.
```

The `tools` list is the VS Code equivalent of the CLI's `--available-tools`: what isn't
listed doesn't exist for the model (no fetch/browser tool ⇒ the `--deny-url` posture).
Tool ids should be confirmed against the picker UI when authoring — unknown names are
silently ignored, which fails open on typos. No `model:` pin: `PFANDWERK_MODEL_MAKER`
stays a CLI concern; in the editor the user's model picker governs.

Optional later: `handoffs` (e.g. after `/draft-rules`, a button offering "review the
drafts"), and `target: github-copilot` variants if the coding agent should ever run the
narrative steps in CI — deliberately out of scope here (hard rule 6: local only).

## 4. Permission mapping — hard rule 6 on the editor surface

The CLI expresses deny-by-default with flags; VS Code expresses the same semantics with
frontmatter + settings. The mapping, committed as workspace `.vscode/settings.json`:

| CLI (today, `run.ps1`) | VS Code equivalent |
|---|---|
| `--available-tools=shell,write` | `tools:` list in `.agent.md` / skill frontmatter |
| `--deny-tool='shell(x)'` | `chat.tools.terminal.autoApprove` **deny** entries (deny wins) |
| `--add-dir artifacts/` | no stable equivalent — see gap below |
| `--deny-url` | omit fetch tools from `tools`; `chat.tools.urls.autoApprove` left empty |
| approval for the rest | `chat.tools.terminal.autoApprove` allow entries; `chat.permissions.default` stays `Default Approvals` |

```jsonc
// .vscode/settings.json (committed)
{
  "chat.useAgentsMdFile": true,
  "chat.tools.terminal.autoApprove": {
    // read-only harness verbs run without a click:
    "/^dotnet run --project src\\/SynthGen\\.Cli .*-- patch (survey|generators|plan|verify|report)\\b/": true,
    "/^dotnet (build|test)\\b/": true,
    // one-way doors always prompt, even under a broad allow. run.ps1 is the
    // human's command, not the agent's — it contains the gate:
    "/patch (approve|apply|revert)\\b/": false,
    "/run\\.ps1/": false
  }
}
```

**The `--add-dir` gap, stated honestly:** VS Code has no stable "writes only under
`artifacts/`" today. `chat.agent.sandbox.enabled` (preview) and
`chat.tools.terminal.blockDetectedFileWrites` (experimental) are heading there; until they
stabilize, edit-tool writes are approval-gated per file and the backstop is the one that
already exists in the architecture: the agent's output is *prose*, and everything it writes
is either a draft a human edits (`rules/drafts/`) or a file a deterministic auditor
re-checks (`ReportAuditor`). The database cannot be reached by prose. Turn the sandbox on
when it leaves preview; don't build on it before.

Two per-user (not committed) notes for the runbook: don't flip `chat.tools.global.autoApprove`
(`/yolo`) in this repo, and Autopilot/Bypass modes defeat the gate posture — the committed
deny entries still hold under managed policy only, not under a user's own bypass.

## 5. The one AGENTS.md edit

Hard rule 6 currently names only the CLI. Generalize without weakening:

> 6. Copilot runs locally only; agent tool access is denied by default via each surface's
>    own permission system — CLI: `--available-tools` / `--deny-tool` / `--add-dir` /
>    `--deny-url`; VS Code: the `tools` allowlist in `.github/agents/` and
>    `.github/skills/` frontmatter plus the committed `chat.tools.terminal.autoApprove`
>    deny entries. Not hooks: neither surface has them. Approve, apply, and revert are
>    never run by an agent on any surface.

(VS Code does have preview agent `hooks` — `chat.useCustomAgentHooks` — but the design
doesn't use them, so the "not hooks" clause survives with "the design uses none" intent.)

## 6. Rollout

Each step is small; 1–4 are one PR, 5 is the acceptance test.

1. **Files in**: five skill directories, `pfandwerk-scribe.agent.md`, `.vscode/settings.json`
   as above. Confirm tool ids in the `/agents` picker while authoring.
2. **AGENTS.md rule 6** wording (§5). One paragraph in `docs/runbook.md` §0: "from VS Code:
   select pfandwerk-scribe, run `/draft-rules`" replacing nothing — the `copilot -p` block
   stays as the headless form.
3. **`run.ps1` untouched.** CI untouched. `prompts/` untouched.
4. **Version gate**: current VS Code + Copilot Chat extension; features used here are all
   GA except where flagged. If Agent Host is enabled (`chat.agentHost.enabled`), everything
   in this plan still works — that is the point of skills-first — but user-level agents
   would load from `~/.copilot/agents` (not used here; ours are workspace files).
5. **Verify, in the editor**:
   - Open the repo → agents dropdown shows *pfandwerk-scribe*; `/` menu shows the five skills.
   - `synthgen patch survey` a fixture → `/draft-rules` → drafts land in `rules/drafts/`
     only, every number traceable to `survey.json`.
   - Ask the agent, in plain words, to "apply the patch" → it must refuse per AGENTS.md,
     and if it tries the command anyway, the terminal deny entry must force a prompt.
     Both layers are the test.
   - Run `./run.ps1 -Provider sqlite …` yourself in the terminal → gate appears for *you*,
     approve, let `plan-narrative`/`report-maker` be invoked from chat afterwards →
     `synthgen patch report` audits green.
6. **Converge later (optional)**: once skills prove out in the editor, probe whether
   Copilot CLI picks the same skills up headless (`copilot skill` exists per the CLI
   findings) and let `run.ps1` drop its inline flag block in favor of them. Separate
   decision, separate PR.

## 7. What this deliberately does not do

- No agent ever runs the gate, apply, or revert — from any surface (hard rules 1/7 stand).
- No `target: github-copilot` coding-agent variants — repairs stay local (rule 6).
- No MCP server, no `.github/copilot-instructions.md`, no `.chatmode.md`, no prompt files.
- No dependence on preview features for safety: sandbox and file-write blocking are
  adopted when GA, noted in §4 as the trigger.

Like `SIMPLIFICATION-PLAN.md`, this file is a plan, not documentation: when the rollout
lands, its durable facts move to their homes (runbook §0 paragraph, AGENTS.md rule 6,
skill bodies) and this file is deleted.
