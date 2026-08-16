-- SQLite mirror of ledger.sql, for the offline fixture. Same keys, same constraints.
CREATE TABLE IF NOT EXISTS dbo.SyntheticLedger (
  TargetTable TEXT NOT NULL,
  RowKey      TEXT NOT NULL,
  ColumnName  TEXT NOT NULL,
  Value       TEXT NOT NULL,
  RuleId      TEXT NOT NULL,
  CreatedAt   TEXT NOT NULL DEFAULT (datetime('now')),
  CreatedBy   TEXT NOT NULL,
  PRIMARY KEY (TargetTable, RowKey, ColumnName),
  UNIQUE (TargetTable, ColumnName, Value)
);
