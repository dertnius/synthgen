# Enterprise / restricted-network setup

How to get everything — including `scratch/adventureworks.db` — working on a machine
where the public internet (GitHub, nuget.org, anaconda.org) is blocked or filtered.

## One command

```bash
powershell -ExecutionPolicy Bypass -File scripts\setup-enterprise.ps1
```

The wizard reconfigures every dependency at once and ends with **proof**: it writes the
repo's `NuGet.config` from your profile, provisions the conda SQLite environment, then
runs `dotnet restore` → `build` → the full test suite and asserts zero skipped tests.
If that finishes, the environment demonstrably works.

The workflow for an enterprise:

1. Fork/mirror this repo internally, edit **[enterprise-profile.psd1](enterprise-profile.psd1)**
   in place (mode, mirror URLs, proxy), commit. Every teammate now runs the wizard with
   zero prompts.
2. Machine-specific values (CA bundle path, personal proxy) go in the gitignored
   `enterprise-profile.local.psd1` — the wizard offers to write it for you after
   interactive prompts. Precedence: **flags > local override > committed profile > prompt**.
3. Agents/CI run `scripts\setup-enterprise.ps1 -NonInteractive` — it never prompts and
   fails fast with a named exit code.

Blast radius, by design: **repo and process scope only.** The wizard never writes
`%USERPROFILE%\.condarc`, user environment variables, or system certificate stores —
conda gets its channel/proxy/CA per-invocation.

### Flags and exit codes

`-Mode mirror|offline` · `-NuGetMirrorUrl` · `-OfflineFeedPath` · `-CondaChannelUrl` ·
`-ProxyUrl` · `-CaBundlePath` · `-NonInteractive` · `-SkipVerify` · `-Force`
(overwrite a differing `NuGet.config`, backup kept)

| Exit | Meaning |
|---|---|
| 0 | Environment proven working |
| 2 | Profile/flag validation failure (missing value in `-NonInteractive`, bad mode/version) |
| 3 | Prerequisite missing (.NET 10+ SDK, conda/micromamba, offline feed) |
| 4 | `NuGet.config` exists and differs; re-run with `-Force` or reconcile |
| 5 | Conda env provisioning failed |
| 6 | `dotnet restore` failed |
| 7 | `dotnet build` failed |
| 8 | Tests failed — or were skipped, which would mean SQLite silently missing |

### Profile schema

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
the wizard has run, generation is 100% offline:

```bash
pwsh samples/adventureworks/run-local.ps1
```

## Network touchpoint inventory (complete)

| # | Touchpoint | When | Public endpoint | Enterprise alternative |
|---|---|---|---|---|
| 1 | .NET SDK 8.x | once per machine | dotnet.microsoft.com | corporate software catalog (SCCM/Intune) |
| 2 | NuGet restore (123 packages, all managed code) | first build | api.nuget.org | internal mirror **or** offline folder feed |
| 3 | conda `sqlite` package | once | conda-forge via anaconda.org | internal conda remote **or** offline package cache |
| — | Generating data, running tests, running the CLI | every run | **none** | — |

Design choices keeping the surface this small: no native binaries via NuGet (the SQLite
provider loads the conda-provisioned `sqlite3.dll` at runtime), no test-time downloads
(Bogus data is embedded, ScriptDom parses offline).

## Air-gapped mode: what the transfer list means

With `Mode = 'offline'` and missing artifacts, the wizard prints a transfer list naming
exactly what to produce on a connected machine:

1. **NuGet feed** — `dotnet restore SynthGen.sln`, then `pwsh scripts/export-offline-feed.ps1`;
   carry the resulting `offline-packages/` folder (~95 MB, 123 `.nupkg`, versions pinned).
   Expect benign `NU1603` "approximate best match" warnings on restore — the feed contains
   the *resolved* closure.
2. **Conda cache** — `conda create -n synthgen-sqlite -c conda-forge sqlite --download-only`;
   copy the `sqlite`/`vc`/`vc14_runtime`/`vs2015_runtime`/`ucrt` archives from that
   machine's `pkgs` cache into `%USERPROFILE%\miniconda3\pkgs`, or into a repo folder
   named by `Conda.OfflinePkgsDir` (e.g. `offline-conda-pkgs/`, gitignored).

Then re-run the wizard: NuGet restores from the folder feed, conda resolves `--offline`.

## Restriction checklist

| Restriction | Where it bites | Answer |
|---|---|---|
| GitHub blocked | — | Nothing is fetched from GitHub; the repo travels via your approved channel. AdventureWorks is schema-compatible DDL authored in-repo, not a download. |
| nuget.org blocked | first restore | Wizard-generated `NuGet.config` starts with `<clear />` — restore uses your mirror or the folder feed, never silently nuget.org. |
| anaconda.org blocked | provisioning | `Conda.ChannelUrl` internal remote, or offline cache transfer. `--override-channels` stops a user `.condarc` from widening sources. |
| Direct binary downloads blocked | any `.exe`/`.dll` fetch | Design baseline: no NuGet native binaries; sqlite3.dll from conda only; SDK/conda from the software catalog. |
| TLS interception | NuGet + conda | NuGet: corporate CA in the Windows trust store (wizard reminds, never modifies). Conda: `Network.CaBundlePath`. |
| Outbound proxy | NuGet + conda | `Network.ProxyUrl`/`NoProxy` — applied per-process and written into `NuGet.config`. |
| PowerShell execution policy | the scripts | `powershell -ExecutionPolicy Bypass -File ...`; after file transfers, `Unblock-File` clears mark-of-the-web. Scripts are plain PS 5.1, no gallery modules. |
| No SQL Server anywhere | E2E validation | `--provider sqlite` runs the full pipeline locally; `--provider sqlserver` stays untouched for when a server exists. |
| Restore audit warnings offline | `dotnet restore` | Fails soft; set `NuGet.DisableAudit = $true` if warnings-as-errors is enforced. |

## Appendix: manual fallback

Every wizard stage can still be done by hand: generate/edit `NuGet.config` yourself
(single source + `<clear />`), run `scripts/setup-sqlite.ps1` directly (`-Channel`,
`-Offline`, `-ProxyUrl`, `-CaBundlePath`, `-PkgsDir`, `-Force`; `-SetUserEnvVar` persists
the dll path for manual workflows — the wizard instead pins it per-process), then
`dotnet restore/build/test`. If you want a machine-wide `.condarc` for *other* conda work
(the wizard neither needs nor writes one):

```yaml
channel_alias: https://artifacts.corp.example/api/conda
channels: [conda-forge]
default_channels: []
ssl_verify: C:\corp\ca-bundle.pem
```
