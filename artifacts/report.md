# Run report (narrative failed audit)

Run `20260817T052819Z-657e` · plan `ad9214c08008` · rules `104ed1e43e33` · approved by runner at 2026-08-17T05:28:21.4473044Z

| Rule | Column | Kind | Patched | Skipped rows |
|---|---|---|---|---|
| AW-PROD-001 | Color | ephemeral | 3 | none |
| AW-ORDER-001 | Status | ephemeral | 3 | none |
| CUR-001 | Name | derived | 14 | CurrencyCode ZZZ |
| AW-CUSTOMER-001 | PersonID | identity | 3 | none |

## New identities

- CustomerID 1 -> `563` (permanent)
- CustomerID 2 -> `588` (permanent)
- CustomerID 3 -> `784` (permanent)

## Verify

- L1 re-scan:  pass
- L2 invariants: pass
- L3 consumer: pass
- regressions: none
- pre-existing failures: none
