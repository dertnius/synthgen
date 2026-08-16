# Data API Builder — pfandwerk's REST write path

`dab-config.json` is the Data API Builder configuration backing `DabPatchSink`, the write
path for sites whose policy requires database writes to pass through an API layer rather
than a direct SQL connection.

Generated and validated with the DAB CLI (`dab init` / `dab add` / `dab configure`), not
hand-written. `dab validate` reports **"The config satisfies the schema requirements"** and
resolves both entities to `/api/Property` and `/api/Security`.

## Prerequisites

```bash
dotnet tool install -g Microsoft.DataApiBuilder     # 2.0.10 at time of writing
export PATH="$PATH:$HOME/.dotnet/tools"
```

**DAB 2.0.10 targets the .NET 8 runtime.** This repository requires .NET 10 or newer, so on
a machine with only .NET 10 installed the tool will refuse to launch with
`You must install or update .NET to run this application`. Roll it forward:

```bash
export DOTNET_ROLL_FORWARD=Major
```

## Connection string

Never written into this file. `data-source.connection-string` is `@env('PFANDWERK_TARGET_CONNECTION')`,
resolved at start-up:

```bash
export PFANDWERK_TARGET_CONNECTION="Server=…;Database=PropertyDev;Integrated Security=true;TrustServerCertificate=true"
```

The same value must satisfy `allowlist.json` (hard rule 5) — DAB does not perform that
check, `pfandwerk plan`'s guard step does, and it runs first.

## Running it

```bash
dab validate                                   # schema + entities + a live connection test
dab start                                      # serves REST on http://localhost:5000/api

# then, from the repo root:
./run.ps1 -Sink dab                            # or -DabUrl http://host:port
pfandwerk apply --sink dab --dab-url http://localhost:5000
```

`--sink sql` is the default and is unaffected by any of this.

`dab start` is a **foreground process for the duration of APPLY and REVERT**, and it is the
operator's responsibility to start and stop it. Nothing in `run.ps1` launches it: a spine
that silently starts a database-facing server would undermine the point of the guard phase.
If `DabPatchSink` is selected and the endpoint is not answering, APPLY aborts rather than
falling back to SQL.

## What the config permits, and what it does not

| Setting | Value | Why |
|---|---|---|
| `entities.*.permissions` | `anonymous: read, update` | Patching needs exactly these two. **No `create`, no `delete`** — a bug in pfandwerk cannot insert or remove a row through this path, only change a column in an existing one |
| `runtime.mcp.enabled` | `false` | DAB 2.0.10 enables an MCP endpoint **by default**. Left on, the target tables would be reachable by any MCP client, including an agent — a direct contradiction of hard rule 1 |
| `runtime.graphql.enabled` | `false` | Unused surface. Also disabled per entity, so re-enabling the global flag does not silently expose both tables |
| `runtime.rest.request-body-strict` | `true` | An unexpected field in a PATCH body is rejected rather than ignored |
| `runtime.telemetry.open-telemetry.enabled` | `false` | Was on by default with an unset endpoint. pfandwerk's trajectory logging comes from the Copilot CLI, not from DAB |
| `runtime.host.authentication.provider` | `Unauthenticated` | Dev/test only. This is why D10's allowlist exists: the API has no auth of its own, so the guard phase is the only thing preventing it being pointed somewhere it should not be |
| `runtime.host.mode` | `development` | Returns detailed errors, which is wanted while building rules and unacceptable anywhere near production. pfandwerk does not target production (§10) |

## How `DabPatchSink` uses it

**Two** requests per row, not one:

```http
GET /api/Security/PropertyId/104          # read the previous value

PATCH /api/Security/PropertyId/104
Content-Type: application/json

{ "SecurityId": "DE000R3X9T05" }
```

The GET is not optional. `patches.jsonl` records what each column held beforehand, and
without it a run cannot be reverted — DAB's PATCH response reflects the new state, not the
old.

Entity names are the unqualified table name: a rule on `dbo.Security` addresses the
`Security` entity, which is how `dab add Security --source dbo.Security` was invoked. A
missing entity is reported once, before any write, with the `dab add` command that fixes it.

### Two limitations worth knowing

**JSON is typed; plan.json stores strings.** Numeric-looking values are sent as JSON
numbers and everything else as strings. That is wrong for a string column whose value
happens to be all digits — `SqlPatchSink` has the same heuristic but the driver reconciles
the mismatch and DAB's `request-body-strict` does not. Use `--sink sql` for such a column.

**Single-column keys only.** DAB's composite-key route is `/api/E/K1/V1/K2/V2`; rules carry
one `key`, so a composite key is refused with a clear message rather than half-supported.

A non-2xx response aborts the run with the status and DAB's own error body. It never falls
back to SQL: quietly taking the path your policy forbids would be worse than stopping.

## The constraint that keeps this optional

DAB cannot enrol the ledger write and the target write in one transaction — they are a SQL
`INSERT` and an HTTP request against separate databases. `SqlPatchSink` can, which is why
it is the default (D12).

On this path a ledger row therefore means **reserved, not applied**. Applied-state derives
from `patches.jsonl`, and `Planner` treats a ledger hit as "reuse this value" without
assuming the database already holds it. A failure between the two leaves a reserved
identity that the next run reuses and re-applies — recoverable, but a state that does not
exist at all on the default sink.

If your site does not mandate an API layer, use `SqlPatchSink` and leave this directory
unused.

## Regenerating

```bash
cd dab
dab init --database-type mssql --connection-string "@env('PFANDWERK_TARGET_CONNECTION')" \
         --host-mode development --rest.enabled true --graphql.enabled false
dab add Property --source "dbo.Property" --permissions "anonymous:read,update"
dab add Security --source "dbo.Security" --permissions "anonymous:read,update"
dab configure --runtime.mcp.enabled false
dab configure --runtime.rest.request-body-strict true
# then re-apply by hand: $schema -> latest/download (the version-pinned URL 404s),
# telemetry off, and graphql.enabled false on each entity — `dab update --graphql.enabled`
# is not accepted by 2.0.10's argument parser.
dab validate
```
