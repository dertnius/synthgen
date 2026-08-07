# Enterprise / restricted-network setup

How to get everything — including `scratch/adventureworks.db` — working on a machine
where the public internet (GitHub, nuget.org, anaconda.org) is blocked or filtered.

## The most important fact first

**`scratch/adventureworks.db` is never downloaded.** It is *generated locally* by this
project from two inputs that are plain text inside the repository:

- [samples/adventureworks/adventureworks.sql](samples/adventureworks/adventureworks.sql) (the DDL)
- [samples/adventureworks/*.rules.yaml](samples/adventureworks/README.md) (the generation rules)

There is no AdventureWorks download, no GitHub fetch, no sample-data package. Once the
project **builds** and the conda SQLite is **provisioned**, database generation is 100%
offline — the only two network-dependent moments in the project's entire life are those
build-time installations, and both can be pointed at internal enterprise sources before
anything is installed.

## Network touchpoint inventory (complete)

| # | Touchpoint | When | Public endpoint | Enterprise alternative |
|---|---|---|---|---|
| 1 | .NET SDK 8.x | once per machine | dotnet.microsoft.com | corporate software catalog (SCCM/Intune/winget internal source) |
| 2 | NuGet restore (123 packages, all managed code) | first build | api.nuget.org | internal mirror (Artifactory/Nexus/Azure Artifacts) **or** offline folder feed |
| 3 | conda `sqlite` package | once, for local SQLite runs | conda-forge via anaconda.org | internal conda remote **or** offline package-cache transfer |
| — | Generating data, running tests*, running the CLI | every run | **none** | — |

\* SQLite-backed tests need touchpoint 3 done once; they skip cleanly (not fail) without it.

Deliberate design choices that keep the surface this small:

- **No native binaries via NuGet**: SQLite support uses `Microsoft.Data.Sqlite.Core` +
  `SQLitePCLRaw.provider.dynamic_cdecl` (both pure managed) and loads the
  conda-provisioned `sqlite3.dll` at runtime. Conda/micromamba is the *only* sanctioned
  binary channel, matching the enterprise policy.
- **No test-time downloads**: Bogus locale data is embedded in its package; ScriptDom
  parses offline; nothing calls out at generation or evaluation time.

## Configure internal sources BEFORE installing

### 1. NuGet — internal mirror or offline feed

Copy [NuGet.enterprise.config.example](NuGet.enterprise.config.example) to
`NuGet.config` in the repo root and edit it. It starts with `<clear />`, so machine-wide
public sources are cut off for this solution *before* the first restore — restores either
use your source or fail loudly; they never silently reach nuget.org.

- **Mirror path**: uncomment the `corp-nuget` source and set your Artifactory / Nexus /
  Azure Artifacts nuget.org-proxy URL.
- **Air-gapped path**: on any connected machine (can be outside the restricted zone),
  restore once, then run:

  ```bash
  pwsh scripts/export-offline-feed.ps1
  ```

  This copies the exact 123-package closure (`.nupkg` files, versions pinned by the
  lockfile-equivalent assets) into `offline-packages/`. Carry that folder (≈95 MB) over
  your approved transfer channel together with the repo, and uncomment the `offline`
  source in `NuGet.config`. Verify with a from-scratch restore that provably uses only
  the folder:

  ```bash
  dotnet restore SynthGen.sln --packages scratch/restore-test --source offline-packages --no-cache
  ```

  Expect a handful of benign `NU1603` warnings ("approximate best match ... resolved"):
  the feed contains the *resolved* closure (e.g. `Microsoft.Extensions.Logging.Abstractions`
  8.0.2, not the 8.0.0 lower bound some packages declare), so the resolver notes the
  substitution. The online restore resolves to exactly the same versions.

Also recommended on restricted machines (silences non-essential outbound calls):

```bash
setx DOTNET_CLI_TELEMETRY_OPTOUT 1
```

and in `NuGet.config` / project properties, disable the NuGet vulnerability-audit fetch
(`<NuGetAudit>false</NuGetAudit>` in a `Directory.Build.props`, or ignore the single
warning — restore still succeeds without it).

### 2. Conda — internal channel or offline cache

Per policy, SQLite comes **only** from conda/micromamba. Three ways, in order of preference:

**a) Internal conda remote (one-off):**

```bash
pwsh scripts/setup-sqlite.ps1 -Channel https://artifacts.corp.example/api/conda/conda-forge-remote
```

**b) Internal remote via `.condarc` (permanent):** copy
[.condarc.enterprise.example](.condarc.enterprise.example) to `%USERPROFILE%\.condarc`,
set `channel_alias` to your repository, then plain `pwsh scripts/setup-sqlite.ps1` works.
`ssl_verify` and `proxy_servers` entries in the same file handle TLS interception and
proxies.

**c) Fully air-gapped:** on a connected machine, download the win-64 archives for
`sqlite` and its dependency chain (`vc`, `vc14_runtime`, `vs2015_runtime`, `ucrt`) —
e.g. `conda create -n synthgen-sqlite -c conda-forge sqlite --download-only` — and copy
everything from that machine's `pkgs` cache into the restricted machine's cache
(`%USERPROFILE%\miniconda3\pkgs`, or a dir listed in `.condarc` `pkgs_dirs`). Then:

```bash
pwsh scripts/setup-sqlite.ps1 -Offline
```

`-Offline` passes conda's `--offline` flag: cache-only resolution, zero network.

If conda/micromamba itself is not installed yet, it must come from your approved software
catalog — the script intentionally refuses to bootstrap it from the internet.

### 3. .NET SDK

Any 8.x SDK. On restricted machines this comes from the corporate catalog; nothing in
this repo downloads or updates SDKs (no `global.json` roll-forward pinning, no workloads).

## Getting `scratch/adventureworks.db`, step by step

Prerequisites done once (previous section): SDK present, NuGet source configured,
conda SQLite provisioned.

```bash
dotnet restore SynthGen.sln
```

```bash
dotnet build SynthGen.sln
```

```bash
dotnet test SynthGen.sln
```

```bash
pwsh samples/adventureworks/run-local.ps1
```

The last command creates the schema from the DDL and loads all seven tables in
dependency order with their evaluations (3,026 rows, 34 checks). Output files:

- `scratch/adventureworks.db` — main database (anchor file)
- `scratch/adventureworks.Production.db`, `scratch/adventureworks.Sales.db` — one file
  per SQL Server schema, attached under the schema's name so `[Sales].[SalesOrderHeader]`
  style queries work verbatim

The run is deterministic (seeds are in the rules files): the same repo state produces the
same database on any machine, which is also your integrity check after a file transfer —
regenerate and compare row counts/evaluations rather than transferring the .db itself.
If you prefer a different location: each `generate` call takes `--connection <path>`, or
pass `-Database <path>` to `run-local.ps1`.

## Restriction checklist (what can bite, and the pre-arranged answer)

| Restriction | Where it bites | Answer |
|---|---|---|
| GitHub blocked | — | Nothing is fetched from GitHub; the repo travels via your approved channel. AdventureWorks is *schema-compatible DDL authored in-repo*, not a download. |
| nuget.org blocked | first `dotnet restore` | `NuGet.config` with `<clear />` + internal mirror or `offline-packages/` folder feed (exporter script provided). |
| anaconda.org blocked | `setup-sqlite.ps1` | `-Channel <internal remote>`, `.condarc` channel_alias, or `-Offline` with transferred package cache. |
| Direct binary downloads blocked | any `.exe`/`.dll` fetch | Already the design baseline: no NuGet native binaries; sqlite3.dll comes from conda only; SDK/conda from the software catalog. |
| TLS interception (corporate CA) | NuGet + conda | NuGet uses the Windows trust store (install the corp CA there); conda: `ssl_verify:` path in `.condarc`. |
| Outbound proxy required | NuGet + conda | `HTTPS_PROXY`/`HTTP_PROXY` env vars cover both; explicit knobs exist in both config templates. |
| PowerShell execution policy | the three `.ps1` scripts | `pwsh -ExecutionPolicy Bypass -File <script>` (scripts are plain, no gallery modules), or run the equivalent CLI commands from TESTING.md by hand. |
| No SQL Server anywhere | E2E validation | that's the point of `--provider sqlite`: full pipeline locally; `--provider sqlserver` stays untouched for when a server exists. |
| Restore audit warnings offline | `dotnet restore` | vulnerability-db fetch fails soft (warning only); disable via `<NuGetAudit>false</NuGetAudit>` if warnings-as-errors is enforced. |
