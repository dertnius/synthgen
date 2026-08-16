# Testing strategy

Production targets SQL Server 2019+ via `SqlBulkCopy`, which cannot run without a real
server. Testing therefore happens on three levels, all runnable on a locked-down machine
(no LocalDB, no Docker, binary downloads blocked by the enterprise network).

## 1. Pure unit tests (no I/O)

Parsing, rules validation, scaffolding, generation determinism, ranges, null rates,
weighted picks, uniqueness (enforced on the post-truncation value), expectation logic —
see [DdlParserTests.cs](tests/SynthGen.Tests/DdlParserTests.cs),
[RulesTests.cs](tests/SynthGen.Tests/RulesTests.cs),
[GenerationTests.cs](tests/SynthGen.Tests/GenerationTests.cs),
[EvaluatorTests.cs](tests/SynthGen.Tests/EvaluatorTests.cs).

## 2. Hand-rolled fakes at the seams (no mocking framework)

The DB touchpoints are three small seams, each swappable:

| Seam | Production | Test double |
|---|---|---|
| `ITableLoader` | `BulkLoader` (SqlBulkCopy) | `FakeTableLoader` captures table name, columns, rows, flags |
| `Func<IDbConnection>` in `Evaluator` | `SqlConnection` | SQLite connection factory |
| `Func<IDbConnection>` in `LookupFetcher` | `SqlConnection` | SQLite connection factory, or canned dictionaries |

Fakes are deliberate: the seams are one method each, so a mocking library would add a
dependency without adding clarity.

## 3. SQLite integration tests (real SQL, fully local)

`SynthGen.Core/Sqlite` runs the identical pipeline — schema creation from the parsed DDL,
generation, loading, Dapper FK lookups, Dapper evaluations — against SQLite files.
[SqliteIntegrationTests.cs](tests/SynthGen.Tests/SqliteIntegrationTests.cs) drives the
full samples flow (Countries → Customers with FK lookups) and asserts every evaluation
passes. This layer already caught a real production bug (Dapper `Query<object>` returning
`DapperRow` instead of scalars in the lookup path) that the pure unit tests could not.

The real-world workout is
[AdventureWorksIntegrationTests.cs](tests/SynthGen.Tests/AdventureWorksIntegrationTests.cs):
a 7-table subset of Microsoft's public AdventureWorks schema
([samples/adventureworks/](samples/adventureworks/README.md)) — two schemas, cross-schema
FK chains, a composite PK with IDENTITY, computed columns, and CHECK-mirroring rules —
loaded in dependency order with every evaluation asserted. The same chain runs via the
CLI with `pwsh samples/adventureworks/run-local.ps1`.

### SQLite provisioning

No NuGet-bundled native binaries are used. Windows enterprise setups provision the native
library through conda or micromamba (conda-forge), while Linux CI may use the distro
library through an explicit path:
`Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.dynamic_cdecl` are pure managed
packages, and [SqliteNative.cs](src/SynthGen.Core/Sqlite/SqliteNative.cs) loads the
conda-provided `sqlite3.dll` at runtime.

```bash
pwsh scripts/setup-sqlite.ps1
```

creates the `synthgen-sqlite` conda env with the sqlite package. (On enterprise networks,
`scripts/setup-enterprise.ps1` runs this for you as part of its one-command setup — manual
invocation remains supported.) Discovery order:

1. `SYNTHGEN_SQLITE_DLL` environment variable (explicit pin)
2. `envs/synthgen-sqlite/Library/bin/sqlite3.dll` in any conda/micromamba root
3. Any other conda env (base included) carrying `sqlite3.dll`

Tests marked `[SqliteFact]` skip with an explanatory message on an unprovisioned local
machine. CI sets `SYNTHGEN_REQUIRE_SQLITE=1`; a missing native library then fails test
discovery instead of silently producing a partial green suite.

On Linux the distro already ships the library, so step 1 alone is enough and conda is not
needed:

```bash
export SYNTHGEN_SQLITE_DLL=/usr/lib/x86_64-linux-gnu/libsqlite3.so.0
dotnet test        # 53 passed, 0 skipped
```

`SqliteNative` loads it through `NativeLibrary.Load`, which accepts a `.so` as readily as a
`.dll`. The GitHub Actions workflow installs Ubuntu's `libsqlite3-0`, resolves its absolute
path with `ldconfig`, and exports it through `SYNTHGEN_SQLITE_DLL` before building and
testing.

### MSSQL-query compatibility trick

Rules files address tables as `[dbo].[Customers]`. SQLite has no schemas, so
`SqliteConnectionFactory` attaches a companion database file per schema
(`test.db` + `test.dbo.db` attached `AS dbo`) — every MSSQL-style query in the samples
runs on SQLite **verbatim**, no rewriting.

### Deliberate fidelity gaps (SQLite is a smoke test, not a SQL Server)

- `decimal` is stored as REAL (float) so numeric aggregates work — precision differs.
- No CHAR padding semantics; no collation parity.
- `IDENTITY` maps to `INTEGER PRIMARY KEY` (rowid): same observable behavior for
  "database assigns 1..N", different internals.
- Computed and rowversion columns are omitted from the SQLite schema.

Final validation against a real SQL Server 2019+ still matters before shipping rules to a
shared environment: `--provider sqlserver` is the default, and nothing in the SQLite path
touches it.

## Local end-to-end (what CI-less verification looks like)

```bash
dotnet test
```

The complete AdventureWorks repair demonstration is:

```powershell
pwsh samples/adventureworks/run-pfandwerk.ps1
```

It generates all seven tables, applies fixed corruption, runs the reviewed plan through
`-Yes` approval, applies and verifies the patch, audits the report, then repeats the
identity gaps with a different seed to prove ledger reuse.

```bash
dotnet run --project src/SynthGen.Cli -- generate --ddl samples/customers.sql --table dbo.Countries --rules samples/countries.rules.yaml --provider sqlite --create-table --connection scratch/local.db
```

```bash
dotnet run --project src/SynthGen.Cli -- generate --ddl samples/customers.sql --table dbo.Customers --rules samples/customers.rules.yaml --provider sqlite --connection scratch/local.db
```

```bash
dotnet run --project src/SynthGen.Cli -- evaluate --rules samples/customers.rules.yaml --provider sqlite --connection scratch/local.db --json
```
