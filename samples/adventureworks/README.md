# AdventureWorks sample

An 8-table subset structurally faithful to Microsoft's public **AdventureWorks** SQL Server
sample database — the "real example" for local integration testing.

| # | Table | Rows | What it exercises |
|---|---|---|---|
| 00 | `Person.Person` | 1000 | valid parent IDs for customer references |
| 01 | `Production.ProductCategory` | 4 | identity PK, unique Name + rowguid, NEWID()/GETDATE() defaults |
| 02 | `Production.ProductSubcategory` | 12 | FK lookup into step 01 |
| 03 | `Production.Product` | 200 | 21 columns, 7 CHECK constraints, nullable FK, nchar/money/decimal |
| 04 | `Sales.SalesTerritory` | 10 | reserved-word column `[Group]`, real territory names |
| 05 | `Sales.Customer` | 300 | computed `AccountNumber` (skipped), valid Person parent lookup |
| 06 | `Sales.SalesOrderHeader` | 500 | computed `TotalDue`, date-ordering CHECKs, weighted status |
| 07 | `Sales.SalesOrderDetail` | 2000 | **composite PK with IDENTITY** (explicit sequence rule), two FKs |

Run it locally (needs the conda/micromamba SQLite — `scripts/setup-sqlite.ps1`):

```bash
pwsh samples/adventureworks/run-local.ps1
```

Or against a real SQL Server (schema must already exist there; drop `--provider`/`--create-table`
and pass your connection string): the same rules files work unchanged — every evaluation
query is written in portable SQL that avoids the computed columns.

The same chain runs in the test suite as `AdventureWorksIntegrationTests` (`[SqliteFact]`);
local runs may skip without a native SQLite library, while CI makes it mandatory.

The complete pfandwerk demonstration adds deterministic corruption and repairs it through
the reviewed sample rules:

```powershell
pwsh samples/adventureworks/run-pfandwerk.ps1
```

It runs generation, corrupts fixed product/order/customer rows, plans and approves with
`-Yes`, applies through `IPatchSink`, verifies the consumer postconditions, and audits the
bare-facts report. A second pass reintroduces the same identity gaps with a different seed;
the append-only ledger must reuse the original values. The focused
`AdventureWorksPfandwerkIntegrationTests` test asserts the same contract offline.

Conventions worth copying into your own rules:

- **Defaulted NOT NULL columns** (`rowguid`, `ModifiedDate`, flags): generate explicitly
  so the rules stay portable to engines without `NEWID()`/`GETDATE()`.
- **CHECK constraints**: encode them in ranges/picks (`Status` 1–5, prices ≥ 0, disjoint
  date ranges for `DueDate >= OrderDate`), then verify with `ck-*` evaluations.
- **Composite PK with IDENTITY**: give the identity member a `sequence` rule — bulk copy
  keeps the values on SQL Server (`KeepIdentity`), SQLite inserts them as-is.
