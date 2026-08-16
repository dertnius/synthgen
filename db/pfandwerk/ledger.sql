-- Identity ledger. Append-only: rows are never updated or deleted, because a value
-- recorded here is the value that column carries forever (hard rule 3).
--
-- Lives in its own configuration database, separate from any target. Run this once per
-- environment; pfandwerk also applies it automatically at the start of a run.
--
-- Embedded into the pfandwerk assembly as `ledger.sql`, so this file and the code cannot
-- disagree.

IF OBJECT_ID('dbo.SyntheticLedger') IS NULL
CREATE TABLE dbo.SyntheticLedger (
  TargetTable sysname       NOT NULL,
  RowKey      nvarchar(128) NOT NULL,
  ColumnName  sysname       NOT NULL,
  Value       nvarchar(400) NOT NULL,
  RuleId      varchar(32)   NOT NULL,
  CreatedAt   datetime2     NOT NULL DEFAULT sysutcdatetime(),
  CreatedBy   nvarchar(128) NOT NULL,
  CONSTRAINT PK_SyntheticLedger PRIMARY KEY (TargetTable, RowKey, ColumnName),
  -- Makes the database enforce collision safety a second time, independently of the
  -- planner's own check. Key widths total 1312 bytes, inside SQL Server's 1700-byte limit.
  CONSTRAINT UQ_Ledger_Value UNIQUE (TargetTable, ColumnName, Value)
);
