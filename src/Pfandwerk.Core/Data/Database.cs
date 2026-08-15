using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Pfandwerk.Core.Rules;
using SynthGen.Sqlite;

namespace Pfandwerk.Core.Data;

public enum Provider { SqlServer, Sqlite }

/// <summary>
/// Opens connections to the target and the ledger. Provider-agnostic on purpose: the
/// fixture end-to-end runs on SQLite offline, the same pipeline runs on SQL Server.
/// </summary>
public sealed class DbContext
{
    private readonly SqliteConnectionFactory? _sqliteTarget;
    private readonly SqliteConnectionFactory? _sqliteLedger;

    public Provider Provider { get; }
    public string TargetConnection { get; }
    public string LedgerConnection { get; }

    public DbContext(Provider provider, string targetConnection, string ledgerConnection)
    {
        Provider = provider;
        TargetConnection = targetConnection;
        LedgerConnection = ledgerConnection;

        if (provider == Provider.Sqlite)
        {
            _sqliteTarget = new SqliteConnectionFactory(targetConnection, new[] { "dbo" });
            _sqliteLedger = new SqliteConnectionFactory(ledgerConnection, new[] { "dbo" });
        }
    }

    public IDbConnection OpenTarget() => Open(TargetConnection, _sqliteTarget);
    public IDbConnection OpenLedger() => Open(LedgerConnection, _sqliteLedger);

    private IDbConnection Open(string connection, SqliteConnectionFactory? sqlite)
    {
        if (Provider == Provider.Sqlite) return sqlite!.Open();
        var c = new SqlConnection(connection);
        c.Open();
        return c;
    }
}

public sealed record LedgerEntry(string TargetTable, string RowKey, string ColumnName,
                                 string Value, string RuleId);

/// <summary>
/// Append-only identity ledger. Rows are never updated or deleted (hard rule 3): a value
/// recorded here is the value that column will carry forever.
/// </summary>
public sealed class LedgerRepository
{
    private readonly DbContext _db;
    public LedgerRepository(DbContext db) => _db = db;

    public void EnsureCreated()
    {
        using var c = _db.OpenLedger();
        var ddl = _db.Provider == Provider.Sqlite
            ? """
              CREATE TABLE IF NOT EXISTS dbo.SyntheticLedger (
                TargetTable TEXT NOT NULL, RowKey TEXT NOT NULL, ColumnName TEXT NOT NULL,
                Value TEXT NOT NULL, RuleId TEXT NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), CreatedBy TEXT NOT NULL,
                PRIMARY KEY (TargetTable, RowKey, ColumnName),
                UNIQUE (TargetTable, ColumnName, Value));
              """
            : """
              IF OBJECT_ID('dbo.SyntheticLedger') IS NULL
              CREATE TABLE dbo.SyntheticLedger (
                TargetTable sysname NOT NULL, RowKey nvarchar(128) NOT NULL,
                ColumnName sysname NOT NULL, Value nvarchar(400) NOT NULL,
                RuleId varchar(32) NOT NULL,
                CreatedAt datetime2 NOT NULL DEFAULT sysutcdatetime(),
                CreatedBy nvarchar(128) NOT NULL,
                CONSTRAINT PK_SyntheticLedger PRIMARY KEY (TargetTable, RowKey, ColumnName),
                CONSTRAINT UQ_Ledger_Value UNIQUE (TargetTable, ColumnName, Value));
              """;
        c.Execute(ddl);
    }

    public string? Lookup(string table, string rowKey, string column)
    {
        using var c = _db.OpenLedger();
        return c.QueryFirstOrDefault<string>(
            "SELECT Value FROM dbo.SyntheticLedger WHERE TargetTable=@t AND RowKey=@k AND ColumnName=@c",
            new { t = table, k = rowKey, c = column });
    }

    public bool ValueTaken(string table, string column, string value)
    {
        using var c = _db.OpenLedger();
        return c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.SyntheticLedger WHERE TargetTable=@t AND ColumnName=@c AND Value=@v",
            new { t = table, c = column, v = value }) > 0;
    }

    public int Count()
    {
        using var c = _db.OpenLedger();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SyntheticLedger");
    }

    public static void Insert(IDbConnection conn, IDbTransaction? tx, LedgerEntry e, string createdBy) =>
        conn.Execute(
            """
            INSERT INTO dbo.SyntheticLedger (TargetTable, RowKey, ColumnName, Value, RuleId, CreatedBy)
            VALUES (@TargetTable, @RowKey, @ColumnName, @Value, @RuleId, @createdBy)
            """,
            new { e.TargetTable, e.RowKey, e.ColumnName, e.Value, e.RuleId, createdBy }, tx);
}
