# Copilot CLI findings (pfandwerk P0b spike)

CLI: GitHub Copilot CLI **1.0.80**, installed via `npm install -g @github/copilot`
(3 packages, 8s). Probed on Linux; flag surface is platform-independent.

**Status: Q1 answered, Q2 partially answered, Q3 blocked on authentication.**

The CLI installs and runs here. It cannot complete a model call, because no GitHub token
is present in this environment — the failure is `No authentication information found`, not
a network error. Independently, the sandbox proxy denies `api.githubcopilot.com:443` with a
403 at CONNECT, so a token alone would not be sufficient here either. Both are environment
facts, not CLI limitations.

---

## Q1 — Can a tool call be denied? **Yes, natively. Hooks are not needed.**

This is the finding that changes the design. Copilot CLI ships a permission system that is
more precise than the pre/post-tool-use hooks §6.18 assumed, and there is no hook mechanism
in the CLI at all (`copilot --help` contains no occurrence of "hook").

Two independent layers:

**Layer 1 — tool visibility.** `--available-tools` exposes *only* the listed tools to the
model; `--excluded-tools` removes specific ones. These filter what the model can see at
all, before any approval logic runs. This is the true deny-by-default primitive.

**Layer 2 — approval.** `--allow-tool` / `--deny-tool` take patterns of the form
`kind(argument)`:

| Kind | Matches |
|---|---|
| `shell(command:*?)` | a shell command; `shell(git:*)` matches `git push` but not `gitea` |
| `write(path?)` | file creation/modification; relative paths match by trailing components, so `write(.env)` matches any directory — use an absolute path to scope to one |
| `url(domain-or-url?)` | URL access, protocol-aware; a bare domain defaults to `https://` |
| `<mcp-server>(tool?)` | a specific MCP tool, or every tool from that server |

**Denial always wins**, including over `--allow-all-tools`.

### The precedence trap

`--allow-all-tools` is *required* for non-interactive mode — without it, `-p` waits for a
confirmation nobody is there to give. Combined with the rule above, this means the obvious
construction is backwards:

```bash
# WRONG — deny-by-default cannot be expressed this way.
# The broad deny wins over the narrow allow, blocking pfandwerk too.
copilot -p … --allow-all-tools --deny-tool='shell' --allow-tool='shell(pfandwerk:*)'
```

`--allow-all-tools` plus denials is **allow-by-default minus exceptions**, which is not what
hard rule 6 requires. Deny-by-default has to come from layer 1:

```bash
copilot -p prompts/plan.md \
  --available-tools='shell,write' \        # nothing else is visible to the model
  --allow-all-tools \                      # required for non-interactive
  --deny-tool='shell(dab:*)' \             # explicit denials still win
  --add-dir "$PWD/artifacts" \
  --deny-url \
  --log-dir artifacts/trajectory --log-level info
```

The exact `--available-tools` token names still need confirming against an authenticated
session; the mechanism is confirmed, the vocabulary is not.

### Other native mechanisms that replace planned work

- **`--add-dir`** scopes filesystem access. Path verification is on by default;
  `--allow-all-paths` disables it. This replaces the hook's "deny writes outside
  `artifacts/`".
- **`--deny-url` / `--allow-url`** are protocol-aware. A blanket `--deny-url` closes network
  egress.
- **`--secret-env-vars`** strips named environment variables from shell and MCP
  environments and redacts them from output. Directly useful for connection strings.
- **Command sandboxing** (experimental, `--experimental`) runs shell commands in an
  OS-level sandbox via Microsoft Execution Containers, enforcing path and network policy.
  Note it *inherits the shell environment apart from a fixed blocklist*, so credentials
  already in the environment stay visible to sandboxed commands.
- **OpenTelemetry monitoring** plus `--log-dir` / `--log-level` are native. The planned
  `post-tool-use.ps1` trajectory logging may reduce to configuring these.
- **`--max-ai-credits`** caps spend per session.

### Consequence for the plan

Hard rule 6 is met by CLI configuration, not by hooks. `hooks/pre-tool-use.ps1` and
`hooks/post-tool-use.ps1` should not be built as specified; §6.18 becomes "compose the
permission flags and verify them", and the P4 gate's "blocked-command test" becomes a test
that asserts a denied command actually fails.

---

## Q2 — The `-p` non-interactive contract. **Partially answered.**

Confirmed from the CLI's own help and behaviour:

- `-p` / `--prompt` runs non-interactively and exits when finished.
- `--allow-all-tools` is mandatory for it, per the flag's own description.
- Unauthenticated, it exits **0** with an error on stdout. That is a trap for
  `run-report.ps1`: exit code alone is not a success signal, so the spine must check that
  the expected output file was actually written.
- `--log-level` separates diagnostics from output; `--no-color` should be set for
  machine-readable capture.

Still unanswered without a token: whether stdout carries prose only or mixes tool chatter.
This decides whether the maker's stdout can be redirected into `report.md` directly.
`prompts/report-maker.md` already instructs the agent to write the file itself, which is
the safer contract and is unaffected either way.

---

## Q3 — Model ids and the cheapest one. **Blocked.**

`--model <model>` exists and accepts `auto`. The help's example shows `--model gpt-5.4`.
Enumerating the real list requires authentication — every model-related invocation returns
`No authentication information found`.

`PFANDWERK_MODEL_MAKER` therefore has no default yet. To finish this, run on an
authenticated machine:

```powershell
copilot --help                       # the flag surface, free
copilot -p "Reply with exactly: ok" --allow-all-tools --model <cheapest>
```

---

## Unplanned finding — `AGENTS.md` is native

`--no-custom-instructions` is described as disabling "loading of custom instructions from
**AGENTS.md** and related files". The CLI loads `AGENTS.md` automatically. The plan chose
that filename as a convention; it is in fact the CLI's own mechanism, which means the hard
rules reach both agents without either prompt having to include them.

`--agent <agent>` also exists for custom agents, alongside `copilot skill` and
`copilot plugin` subcommands. Whether a native agent definition is a better fit than
`-p <prompt file>` is worth one follow-up probe, but `-p` is sufficient and is what
`run.ps1` currently assumes.

---

## Conclusion

- Hooks can deny tool calls: **n/a — the CLI has no hooks, and does not need them**
- Enforcement chosen for hard rule 6: **native permissions** —
  `--available-tools` for visibility, `--deny-tool` for exceptions, `--add-dir` for paths,
  `--deny-url` for network
- `-p` stdout usable as a report body: **undetermined**; the agent writes the file itself
  regardless, and the spine must verify the file exists rather than trusting exit 0
- Cheapest model id: **unknown — requires an authenticated session**
