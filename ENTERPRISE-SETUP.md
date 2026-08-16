# Enterprise / restricted-network setup

How to get everything — including `scratch/adventureworks.db` — working on a machine
where the public internet (GitHub, nuget.org, anaconda.org) is blocked or filtered.

> **Status: frozen since 2026-08.** The wizard and the offline-feed exporter live in
> [`scripts/enterprise/`](scripts/enterprise/) and are kept for restricted-network sites,
> not actively maintained. Neither CI uses them; `scripts/setup-sqlite.ps1` is the
> maintained provisioning path. Flags, exit codes, and the air-gap transfer list are
> documented in the script headers themselves — this file only covers the profile.

## One command

```bash
powershell -ExecutionPolicy Bypass -File scripts\enterprise\setup-enterprise.ps1
```

The wizard reconfigures every dependency at once and ends with **proof**: it writes the
repo's `NuGet.config` from your profile, provisions the conda SQLite environment, then
runs `dotnet restore` → `build` → the full test suite and asserts zero skipped tests.

The workflow for an enterprise:

1. Fork/mirror this repo internally, edit **[enterprise-profile.psd1](enterprise-profile.psd1)**
   in place (mode, mirror URLs, proxy), commit. Every teammate now runs the wizard with
   zero prompts.
2. Machine-specific values (CA bundle path, personal proxy) go in the gitignored
   `enterprise-profile.local.psd1` — the wizard offers to write it for you after
   interactive prompts. Precedence: **flags > local override > committed profile > prompt**.
3. Agents/CI run `scripts\enterprise\setup-enterprise.ps1 -NonInteractive` — it never
   prompts and fails fast with a named exit code (see the script header).

Blast radius, by design: **repo and process scope only.** The wizard never writes
`%USERPROFILE%\.condarc`, user environment variables, or system certificate stores.

## Profile schema

All fields live in [enterprise-profile.psd1](enterprise-profile.psd1) (commented). Summary:

| Field | Purpose |
|---|---|
| `Mode` | `mirror` (internal remotes) or `offline` (folder feed + conda cache) |
| `NuGet.MirrorUrl` | v3 index URL of your internal nuget.org proxy |
| `NuGet.OfflineFeedPath` | repo-relative folder feed (default `offline-packages`) |
| `NuGet.GlobalPackagesFolder` | optional in-repo package cache redirect |
| `NuGet.DisableAudit` | `$true` silences the vulnerability-audit outbound fetch |
| `Conda.ChannelUrl` | internal conda remote (empty = public conda-forge, warned) |
| `Conda.OfflinePkgsDir` | repo-relative transferred conda archives (air-gap) |
| `Network.ProxyUrl` / `NoProxy` | outbound proxy for the wizard's child processes |
| `Network.CaBundlePath` | PEM bundle for conda under TLS interception |
| `Dotnet.TelemetryOptOut` | sets `DOTNET_CLI_TELEMETRY_OPTOUT=1` per-process |

## The most important fact

**`scratch/adventureworks.db` is never downloaded.** It is *generated locally* from plain
text in this repository (`samples/adventureworks/adventureworks.sql` + rules YAML). Once
the wizard has run, generation is 100% offline: `pwsh samples/adventureworks/run-local.ps1`.

The only network touchpoints, ever: the .NET SDK (software catalog), NuGet restore
(mirror or folder feed), and the conda `sqlite` package (internal remote or transferred
cache). Design choices keeping the surface this small: no native binaries via NuGet (the
SQLite provider loads the conda-provisioned `sqlite3.dll` at runtime), no test-time
downloads (Bogus data is embedded, ScriptDom parses offline).

## Restriction checklist

| Restriction | Answer |
|---|---|
| GitHub blocked | Nothing is fetched from GitHub; the repo travels via your approved channel. AdventureWorks is schema-compatible DDL authored in-repo, not a download. |
| nuget.org blocked | Wizard-generated `NuGet.config` starts with `<clear />` — restore uses your mirror or the folder feed, never silently nuget.org. |
| anaconda.org blocked | `Conda.ChannelUrl` internal remote, or offline cache transfer. `--override-channels` stops a user `.condarc` from widening sources. |
| Direct binary downloads blocked | Design baseline: no NuGet native binaries; sqlite3.dll from conda only; SDK/conda from the software catalog. |
| TLS interception | NuGet: corporate CA in the Windows trust store (wizard reminds, never modifies). Conda: `Network.CaBundlePath`. |
| Outbound proxy | `Network.ProxyUrl`/`NoProxy` — applied per-process and written into `NuGet.config`. |
| PowerShell execution policy | `powershell -ExecutionPolicy Bypass -File ...`; after file transfers, `Unblock-File` clears mark-of-the-web. Plain PS 5.1, no gallery modules. |
| No SQL Server anywhere | `--provider sqlite` runs the full pipeline locally; `--provider sqlserver` stays untouched for when a server exists. |
| Restore audit warnings offline | Fails soft; set `NuGet.DisableAudit = $true` if warnings-as-errors is enforced. |

Every wizard stage can still be done by hand: write `NuGet.config` yourself (single
source + `<clear />`), run `scripts/setup-sqlite.ps1` directly, then
`dotnet restore/build/test`.
