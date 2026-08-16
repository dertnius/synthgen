using System.Data;
using Microsoft.Data.SqlClient;
using SynthGen.Sqlite;

namespace SynthGen.Core.Support;

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
            // Keep the offline provider able to exercise the multi-schema AdventureWorks
            // sample as well as the single-schema pfandwerk fixtures.
            var schemas = new[] { "dbo", "Production", "Sales" };
            _sqliteTarget = new SqliteConnectionFactory(targetConnection, schemas);
            _sqliteLedger = new SqliteConnectionFactory(ledgerConnection, schemas);
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
